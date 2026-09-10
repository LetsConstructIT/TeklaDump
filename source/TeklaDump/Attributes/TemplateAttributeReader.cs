using System;
using System.Collections;
using System.Collections.Generic;
using Tekla.Structures.Model;

namespace TeklaDump.Attributes;

/// <summary>
/// Reads template (report) attributes off a <see cref="ModelObject"/> — tier T1: batched per
/// object.
/// </summary>
/// <remarks>
/// Values are fetched with ONE <c>GetAllReportProperties</c> call per object rather than one per
/// name: the catalog already knows whether a name is a string, a double or an integer, so nothing
/// has to be probed by trial and error. Three interop calls per object, not three times N.
/// <para>
/// Report reads cannot batch ACROSS objects, only across names, which is exactly why T2 (the
/// whole-model report join) exists above <see cref="DumpOptions.ReportJoinThreshold"/>.
/// </para>
/// </remarks>
internal sealed class TemplateAttributeReader
{
    /// <summary>
    /// Reads <paramref name="definitions"/> off <paramref name="target"/>.
    /// </summary>
    /// <returns>
    /// Name to value, in the order the definitions were given. Names the object has no value for
    /// are absent: an attribute that does not apply is noise, not data.
    /// </returns>
    public IReadOnlyList<KeyValuePair<string, object>> Read(
        ModelObject target,
        IReadOnlyList<TemplateAttributeDefinition> definitions,
        out string? error)
    {
        error = null;
        if (definitions.Count == 0) return EmptyResult;

        var strings = new ArrayList();
        var doubles = new ArrayList();
        var integers = new ArrayList();

        foreach (var definition in definitions)
        {
            switch (definition.ValueType)
            {
                case TemplateValueType.Float: doubles.Add(definition.Name); break;
                case TemplateValueType.Integer: integers.Add(definition.Name); break;
                default: strings.Add(definition.Name); break;
            }
        }

        var values = new Hashtable();
        try
        {
            target.GetAllReportProperties(strings, doubles, integers, ref values);
            values ??= new Hashtable();
        }
        catch (Exception ex)
        {
            // One object's failed read is a warning on that object, never the end of the run.
            error = ex.Message;
            return EmptyResult;
        }

        var results = new List<KeyValuePair<string, object>>(definitions.Count);
        foreach (var definition in definitions)
        {
            var value = values[definition.Name];
            if (value is not null)
                results.Add(new KeyValuePair<string, object>(definition.Name, value));
        }

        return results;
    }

    /// <summary>
    /// Second pass for explicitly requested names that came back empty in their assumed bucket.
    /// </summary>
    /// <remarks>
    /// A name the catalog never declared is assumed to be CHARACTER, which is wrong for roughly
    /// half of the real cases (someone types <c>WEIGHT_NET</c>). Rather than guess harder, the
    /// names that produced nothing are retried in the double and integer buckets — two extra
    /// interop calls, and only for objects where an explicit name actually missed.
    /// </remarks>
    public IReadOnlyList<KeyValuePair<string, object>> ReadFallback(
        ModelObject target,
        IReadOnlyList<string> missingNames)
    {
        if (missingNames.Count == 0) return EmptyResult;

        var results = new List<KeyValuePair<string, object>>(missingNames.Count);
        var found = new HashSet<string>(StringComparer.Ordinal);

        foreach (var asDouble in new[] { true, false })
        {
            var names = new ArrayList();
            foreach (var name in missingNames)
                if (!found.Contains(name)) names.Add(name);
            if (names.Count == 0) break;

            var values = new Hashtable();
            try
            {
                if (asDouble) target.GetDoubleReportProperties(names, ref values);
                else target.GetIntegerReportProperties(names, ref values);
            }
            catch (Exception)
            {
                continue;
            }

            if (values is null) continue;
            foreach (var name in missingNames)
            {
                if (found.Contains(name)) continue;
                var value = values[name];
                if (value is null) continue;
                results.Add(new KeyValuePair<string, object>(name, value));
                found.Add(name);
            }
        }

        return results;
    }

    private static readonly KeyValuePair<string, object>[] EmptyResult = new KeyValuePair<string, object>[0];
}
