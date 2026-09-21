using System;
using System.Collections;
using System.Collections.Generic;
using Tekla.Structures.Model;
using TeklaDump.Attributes;
using TeklaDump.Attributes.ReportJoin;
using TeklaDump.Json;
using TeklaDump.Session;
using TeklaDump.Sinks;
using TeklaDump.Values;

namespace TeklaDump;

/// <summary>
/// Per-run state: the options, the session, the caches every extractor shares, and the warning
/// collector. One instance per <see cref="DumpWriter"/> call, never shared between runs.
/// </summary>
internal sealed class DumpContext
{
    private readonly List<DumpWarning> _warnings = new List<DumpWarning>();
    private readonly TemplateAttributeReader _templateReader = new TemplateAttributeReader();
    private readonly Dictionary<string, PlannedAttributes> _plansByContentType =
        new Dictionary<string, PlannedAttributes>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _warnedOnce = new HashSet<string>(StringComparer.Ordinal);

    private TemplateAttributeCatalog? _templateCatalog;
    private ComponentAttributeCatalog? _componentCatalog;

    internal DumpContext(DumpOptions options, SessionInfo? session)
    {
        Options = options;
        Session = session;
    }

    public DumpOptions Options { get; }

    /// <summary>Null when there is no Tekla session — a supported state, see <see cref="SessionInfo"/>.</summary>
    public SessionInfo? Session { get; }

    public UnitPolicy Units => Options.Units;

    public IReadOnlyList<DumpWarning> Warnings => _warnings;

    /// <summary>"T0", "T1" or "T2" — set by <see cref="DumpWriter"/> once the tier is decided.</summary>
    internal string AttributeTier { get; set; } = "T0";

    /// <summary>The joined report rows, when the run chose T2. Null in every other tier.</summary>
    internal ReportJoinResult? ReportJoin { get; set; }

    /// <summary>Every attribute name the run actually asked for, for the header's <c>attributeSet</c>.</summary>
    internal SortedSet<string> RequestedAttributeNames { get; } = new SortedSet<string>(StringComparer.Ordinal);

    public void Warn(string code, string message, ModelObject? about = null)
    {
        _warnings.Add(new DumpWarning(code, message, about is null ? null : GuidOf(about), about?.GetType().Name));
    }

    /// <summary>
    /// Records a warning at most once per <paramref name="key"/>. For conditions that are true for
    /// every object in the run — stale numbering, an empty catalog — where one warning is
    /// information and 200,000 are a denial of service.
    /// </summary>
    public void WarnOnce(string key, string code, string message)
    {
        if (_warnedOnce.Add(key)) _warnings.Add(new DumpWarning(code, message));
    }

    /// <summary>
    /// The object's stable GUID, resolved through the session when the identifier carries none.
    /// Null for an uninserted object, which has neither — those emit <c>objectType</c> alone.
    /// </summary>
    public string? GuidOf(ModelObject modelObject)
    {
        try
        {
            var direct = ValueCoercion.GuidOf(modelObject.Identifier);
            if (direct is not null) return direct;
        }
        catch (Exception)
        {
            // No identifier at all (an object constructed but never inserted).
            return null;
        }

        return Session?.ResolveGuid(modelObject);
    }

    /// <summary>
    /// A related object as <c>{objectType, guid}</c>. References are NEVER followed: that is what
    /// keeps a dump from dragging the whole model graph in, and what makes cycles a non-issue
    /// rather than a cycle detector.
    /// </summary>
    public JsonValue? Reference(ModelObject? target)
    {
        if (target is null) return null;

        var reference = new JsonObject();
        reference.Add("objectType", JsonValue.String(target.GetType().Name));
        var guid = GuidOf(target);
        if (guid is not null) reference.Add("guid", JsonValue.String(guid));
        return reference;
    }

    /// <summary>References for an <c>ArrayList</c> or enumerator of model objects.</summary>
    public JsonValue? References(IEnumerable? targets)
    {
        if (targets is null) return null;

        var array = new JsonArray();
        foreach (var item in targets)
        {
            var reference = Reference(item as ModelObject);
            if (reference is not null) array.Add(reference);
        }

        return array.Count == 0 ? null : array;
    }

    /// <summary>
    /// Reads a property that may throw, returning null instead. Tekla properties throw for reasons
    /// that have nothing to do with the caller (an object detached from the model, a member the
    /// running version does not populate), and one such throw must not cost the whole record.
    /// </summary>
    public T? Try<T>(Func<T?> read) where T : class
    {
        try { return read(); }
        catch (Exception) { return null; }
    }

    /// <summary>Value-type sibling of <see cref="Try{T}(Func{T})"/>.</summary>
    public T? TryValue<T>(Func<T> read) where T : struct
    {
        try { return read(); }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Writes the attribute sections that follow every record's <c>create</c>/<c>derived</c>:
    /// user properties, template attributes and — for components — component attributes.
    /// </summary>
    internal void WriteAttributes(ModelObject source, IDumpSink sink)
    {
        WriteUserProperties(source, sink);
        WriteTemplateAttributes(source, sink);
    }

    private void WriteUserProperties(ModelObject source, IDumpSink sink)
    {
        if (Options.UserProperties == UserPropertyMode.None) return;

        var properties = UserPropertyReader.Read(source, out var error);
        if (error is not null)
        {
            Warn("uda-read-failed", "Could not read user properties: " + error, source);
            return;
        }

        if (properties.Count == 0) return;

        sink.BeginSection("userProperties");
        foreach (var property in properties)
            sink.Write(property.Key, ValueCoercion.UserPropertyValue(property.Value));
        sink.EndSection();
    }

    private void WriteTemplateAttributes(ModelObject source, IDumpSink sink)
    {
        if (AttributeTier == "T0") return;

        var contentType = TeklaContentTypes.For(source);
        if (contentType is null) return;

        if (ReportJoin is not null)
        {
            WriteJoinedAttributes(source, sink);
            return;
        }

        var plan = PlanFor(contentType);
        if (plan.Definitions.Count == 0) return;

        var values = _templateReader.Read(source, plan.Definitions, out var error);
        if (error is not null)
        {
            Warn("template-read-failed", "Could not read template attributes: " + error, source);
            return;
        }

        var written = new HashSet<string>(StringComparer.Ordinal);
        sink.BeginSection("templateAttributes");
        foreach (var value in values)
        {
            written.Add(value.Key);
            sink.Write(value.Key, ValueCoercion.TemplateValue(value.Key, value.Value, Units));
        }

        // Explicit names that produced nothing get one retry in the numeric buckets; see
        // TemplateAttributeReader.ReadFallback for why the first guess is often wrong.
        if (plan.IsExplicit)
        {
            var missing = new List<string>();
            foreach (var definition in plan.Definitions)
                if (!written.Contains(definition.Name)) missing.Add(definition.Name);

            foreach (var value in _templateReader.ReadFallback(source, missing))
                sink.Write(value.Key, ValueCoercion.TemplateValue(value.Key, value.Value, Units));
        }

        sink.EndSection();
    }

    private void WriteJoinedAttributes(ModelObject source, IDumpSink sink)
    {
        var guid = GuidOf(source);
        if (guid is null) return;

        var row = ReportJoin!.Row(guid);
        if (row is null || row.Count == 0) return;

        sink.BeginSection("templateAttributes");
        foreach (var value in row)
            sink.Write(value.Key, ValueCoercion.TemplateValue(value.Key, value.Value, Units));
        sink.EndSection();
    }

    /// <summary>The template catalog for the connected model, loaded once per run.</summary>
    internal TemplateAttributeCatalog TemplateCatalog =>
        _templateCatalog ??= TemplateAttributeCatalogProvider.Load(Session?.ModelPath);

    internal ComponentAttributeCatalog ComponentCatalog =>
        _componentCatalog ??= ComponentAttributeCatalog.Create(Session?.ModelPath);

    /// <summary>
    /// The attribute plan for one content type, computed once and reused for every object of that
    /// type — the selection walk is pure and re-running it per object would be the single most
    /// wasteful thing in a 200k-object dump.
    /// </summary>
    internal PlannedAttributes PlanFor(string contentType)
    {
        if (_plansByContentType.TryGetValue(contentType, out var cached)) return cached;

        var declared = TemplateCatalog.ForContentType(contentType);
        if (declared.Count == 0 && TemplateCatalog.IsEmpty)
        {
            WarnOnce(
                "template-catalog-empty",
                "template-catalog-empty",
                "No contentattributes*.lst was found, so no template attributes can be named. " +
                "Searched " + TemplateCatalog.SearchedDirectories.Count + " folder(s) under the model, " +
                "XS_PROJECT, XS_FIRM, XS_SYSTEM, XS_TPLED_INI and the Template Editor settings.");
        }

        var definitions = TemplateAttributeSelection.Plan(
            declared,
            Options.TemplateAttributes,
            Options.TemplateAttributeNames,
            Options.MaxTemplateAttributes,
            out var truncated);

        if (truncated)
        {
            WarnOnce(
                "template-truncated-" + contentType,
                "template-attributes-truncated",
                contentType + ": stopped after " + Options.MaxTemplateAttributes +
                " attributes. Raise MaxTemplateAttributes, or name the ones you need explicitly.");
        }

        foreach (var definition in definitions) RequestedAttributeNames.Add(definition.Name);

        var plan = new PlannedAttributes(
            definitions,
            Options.TemplateAttributeNames is not null && Options.TemplateAttributeNames.Count > 0);
        _plansByContentType[contentType] = plan;
        return plan;
    }

    /// <summary>What a run reads for one content type.</summary>
    internal sealed class PlannedAttributes
    {
        public PlannedAttributes(IReadOnlyList<TemplateAttributeDefinition> definitions, bool isExplicit)
        {
            Definitions = definitions;
            IsExplicit = isExplicit;
        }

        public IReadOnlyList<TemplateAttributeDefinition> Definitions { get; }

        /// <summary>True when the names came from the caller rather than from a scope.</summary>
        public bool IsExplicit { get; }
    }
}
