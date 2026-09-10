using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Tekla.Structures.Model;
using TeklaDump.Json;
using TeklaDump.Sinks;
using TeklaDump.Values;
using TSAngle = Tekla.Structures.Datatype.Angle;
using TSDistance = Tekla.Structures.Datatype.Distance;
using TSIdentifier = Tekla.Structures.Identifier;
using TSPoint = Tekla.Structures.Geometry3d.Point;
using TSVector = Tekla.Structures.Geometry3d.Vector;

namespace TeklaDump.Extractors;

/// <summary>
/// Reads an object by reflection when no extractor claims its type. INSPECT MODE ONLY.
/// </summary>
/// <remarks>
/// Lifted from TeklaLookup's <c>JsonObjectDumper</c> and demoted: there it was the whole engine,
/// here it is the last resort.
/// <para>
/// Bulk mode never reaches this, and the reason is the danger list. A reflection walker calls every
/// public getter it finds, and on a Tekla object some of those getters compute a solid, resolve an
/// assembly or walk the model graph. It cannot know which — so it is fine for the handful of
/// objects an inspect run touches and completely unusable for 200,000.
/// </para>
/// <para>
/// Everything it emits lands under <c>derived</c>. A reflected value has not been checked for
/// whether it can be assigned back, and putting an unverified key in <c>create</c> is exactly the
/// mistake the create/derived split exists to prevent.
/// </para>
/// </remarks>
internal sealed class ReflectionFallback
{
    /// <summary>Members that carry no recreation value or would drag the whole model graph in.</summary>
    private static readonly HashSet<string> BlockedMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Identifier", "Father", "ModificationStamp", "Handle",
    };

    private readonly DumpContext _context;

    public ReflectionFallback(DumpContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Writes the reflected view of <paramref name="source"/> into the record's <c>derived</c>
    /// section, plus a note naming the type so nobody mistakes this for a curated record.
    /// </summary>
    public void Write(ModelObject source, IDumpSink sink)
    {
        sink.BeginSection("derived");
        sink.Write("$fallback", JsonValue.String(
            "No extractor is registered for " + source.GetType().Name +
            "; these fields were read by reflection and none of them is known to be settable."));

        var visited = new HashSet<object>(ReferenceComparer.Instance);
        foreach (var member in GetMembers(source.GetType()))
        {
            if (BlockedMembers.Contains(member.Name)) continue;

            object? raw;
            try { raw = member.Get(source); }
            catch (Exception) { continue; }

            raw = MaterializeIfEnumerator(raw);
            sink.Write(member.Name, BuildValue(raw, 1, visited));
        }

        sink.EndSection();
    }

    /// <returns>The JSON node, or null to signal "omit" (null / empty / cyclic / unreadable).</returns>
    private JsonValue? BuildValue(object? value, int depth, HashSet<object> visited)
    {
        if (value is null) return null;

        switch (value)
        {
            case string s: return ValueCoercion.Text(s);
            case bool b: return JsonValue.Bool(b);
            case char c: return JsonValue.String(c.ToString());
            case Enum e: return JsonValue.String(e.ToString());
            case byte or sbyte or short or ushort or int or uint or long:
                return JsonValue.Number(Convert.ToInt64(value, CultureInfo.InvariantCulture));
            case ulong ul: return JsonValue.Number((double)ul);
            case float or double or decimal:
                return JsonValue.Number(Convert.ToDouble(value, CultureInfo.InvariantCulture));
            case DateTime dt: return JsonValue.String(dt.ToString("o", CultureInfo.InvariantCulture));
            case Guid g: return JsonValue.String(g.ToString());
            case TimeSpan ts: return JsonValue.String(ts.ToString());
        }

        // Tekla value types collapsed to their meaningful, API-shaped form.
        switch (value)
        {
            case TSIdentifier id: return ValueCoercion.GuidOf(id) is string guid ? JsonValue.String(guid) : null;
            case TSVector vector: return ValueCoercion.Vector(vector);
            case TSPoint point: return ValueCoercion.Point(point);
            case Profile profile: return ValueCoercion.Profile(profile);
            case Material material: return ValueCoercion.Material(material);
            case TSDistance distance: return ValueCoercion.Distance(distance, _context.Units);
            case TSAngle angle: return ValueCoercion.Angle(angle, _context.Units);
        }

        // A model object reached through a property is a REFERENCE, never a nested record. That is
        // the same rule the curated extractors follow, and the reason a cycle cannot happen.
        if (value is ModelObject modelObject) return _context.Reference(modelObject);

        if (value is IDictionary dictionary) return BuildDictionary(dictionary, depth, visited);
        if (value is IEnumerable enumerable) return BuildArray(enumerable, depth, visited);

        if (depth >= _context.Options.MaxDepth)
            return JsonValue.String(value.ToString() ?? value.GetType().Name);

        var obj = new JsonObject();
        PopulateReflected(obj, value, depth, visited);
        return obj.Count == 0 ? null : obj;
    }

    private void PopulateReflected(JsonObject obj, object value, int depth, HashSet<object> visited)
    {
        var type = value.GetType();
        var track = !type.IsValueType;
        if (track && !visited.Add(value)) return; // cycle — leave what we have
        try
        {
            foreach (var member in GetMembers(type))
            {
                if (BlockedMembers.Contains(member.Name)) continue;

                object? raw;
                try { raw = member.Get(value); }
                catch (Exception) { continue; }

                raw = MaterializeIfEnumerator(raw);
                var node = BuildValue(raw, depth + 1, visited);
                if (node is not null) obj.Add(member.Name, node);
            }
        }
        finally
        {
            if (track) visited.Remove(value);
        }
    }

    private JsonValue? BuildDictionary(IDictionary dictionary, int depth, HashSet<object> visited)
    {
        var obj = new JsonObject();
        foreach (DictionaryEntry entry in dictionary)
        {
            var node = BuildValue(entry.Value, depth + 1, visited);
            if (node is not null) obj.Add(entry.Key?.ToString() ?? "<null>", node);
        }
        return obj.Count == 0 ? null : obj;
    }

    private JsonValue? BuildArray(IEnumerable enumerable, int depth, HashSet<object> visited)
    {
        var array = new JsonArray();
        foreach (var item in enumerable)
        {
            if (array.Count >= _context.Options.MaxItems) break;
            var node = BuildValue(item, depth + 1, visited);
            if (node is not null) array.Add(node);
        }
        return array.Count == 0 ? null : array;
    }

    /// <summary>
    /// Drains single-pass enumerables and enumerators into a list, leaving strings, dictionaries
    /// and already-materialized collections alone.
    /// </summary>
    /// <remarks>
    /// Tekla's <c>ModelObjectEnumerator</c> implements <see cref="IEnumerator"/> but NOT
    /// <see cref="IEnumerable"/>, so an IEnumerable-only check walks straight past it.
    /// </remarks>
    private static object? MaterializeIfEnumerator(object? raw)
    {
        if (raw is null) return null;
        if (raw is string) return raw;
        if (raw is IDictionary) return raw;
        if (raw is ICollection) return raw;

        if (raw is IEnumerable enumerable)
        {
            var list = new List<object?>();
            foreach (var item in enumerable) list.Add(item);
            return list;
        }

        if (raw is IEnumerator enumerator)
        {
            var list = new List<object?>();
            while (enumerator.MoveNext()) list.Add(enumerator.Current);
            return list;
        }

        return raw;
    }

    private static IEnumerable<ReflectedMember> GetMembers(Type type)
    {
        var properties = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .Select(p => new ReflectedMember(p.Name, target => p.GetValue(target)));

        // Tekla geometry types expose X/Y/Z (and similar) as public FIELDS, not properties, so
        // public instance fields are included too — minus compiler backing fields and any name a
        // property already covers.
        var propertyNames = new HashSet<string>(
            type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name));
        var fields = type
            .GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Where(f => !f.Name.StartsWith("<", StringComparison.Ordinal))
            .Where(f => !propertyNames.Contains(f.Name))
            .Select(f => new ReflectedMember(f.Name, target => f.GetValue(target)));

        // Ordinal by name: reflection order is not guaranteed stable, and the golden files are.
        return properties.Concat(fields).OrderBy(m => m.Name, StringComparer.Ordinal);
    }

    private readonly struct ReflectedMember
    {
        public ReflectedMember(string name, Func<object, object?> get)
        {
            Name = name;
            Get = get;
        }

        public string Name { get; }
        public Func<object, object?> Get { get; }
    }

    private sealed class ReferenceComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceComparer Instance = new ReferenceComparer();
        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
