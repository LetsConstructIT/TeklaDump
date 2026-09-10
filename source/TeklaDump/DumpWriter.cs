using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Tekla.Structures.Model;
using TeklaDump.Attributes;
using TeklaDump.Attributes.ReportJoin;
using TeklaDump.Extractors;
using TeklaDump.Json;
using TeklaDump.Session;
using TeklaDump.Sinks;

namespace TeklaDump;

/// <summary>
/// The entire public entry surface: two methods, two modes, one set of extractors behind both.
/// </summary>
/// <remarks>
/// Neither method enumerates, selects or filters anything. They serialize the objects they are
/// handed, in the order they are handed them — sorting, filtering and collecting are the caller's
/// job (the CLI does it; so can your own LINQ). That non-goal is what keeps this library from
/// growing into a query engine.
/// </remarks>
public static class DumpWriter
{
    /// <summary>
    /// A curated, LLM-oriented view of a handful of objects: header plus one record each.
    /// </summary>
    /// <remarks>
    /// Unregistered types fall back to a reflection read, which is affordable here and nowhere
    /// else. Intended for tens of objects, not thousands — for thousands use <see cref="Bulk"/>.
    /// </remarks>
    public static JsonValue Inspect(IEnumerable<ModelObject> objects, DumpOptions? options = null)
    {
        if (objects is null) throw new ArgumentNullException(nameof(objects));

        var run = new Run(options);
        var list = objects.Where(o => o is not null).ToList();

        run.DecideTier(list, list.Count);

        var sink = new TreeSink();
        foreach (var modelObject in list)
        {
            run.Context.Options.Cancellation.ThrowIfCancellationRequested();
            run.WriteRecord(modelObject, sink, allowReflectionFallback: true, abort: sink.AbortRecord);
        }

        var document = new JsonObject();
        document.Add("header", run.BuildHeader(list.Count));
        document.Add("objects", sink.Records);

        // Warnings ride in the document itself for inspect mode: there is no DumpResult to carry
        // them, and a document that quietly dropped an object would be indistinguishable from one
        // where the object was never selected.
        if (run.Context.Warnings.Count > 0)
            document.Add("warnings", WarningsValue(run.Context.Warnings));

        return document;
    }

    /// <summary>
    /// Streaming NDJSON for arbitrarily many objects: header line, then one line per record. No
    /// reflection, no accumulation, no unbounded memory.
    /// </summary>
    public static DumpResult Bulk(IEnumerable<ModelObject> objects, Stream output, DumpOptions? options = null)
    {
        if (objects is null) throw new ArgumentNullException(nameof(objects));
        if (output is null) throw new ArgumentNullException(nameof(output));

        var stopwatch = Stopwatch.StartNew();
        var run = new Run(options);

        // Buffered prefix: enough to decide the tier honestly without materializing a 300k-object
        // model. If the sequence ends inside the prefix we also know the exact count, which is the
        // only way objectCount can appear in the header of a streamed file.
        var threshold = Math.Max(0, run.Context.Options.ReportJoinThreshold);
        var prefix = new List<ModelObject>(Math.Min(threshold + 1, 8192));
        var rest = objects.Where(o => o is not null).GetEnumerator();
        var exhausted = true;
        while (prefix.Count <= threshold)
        {
            if (!rest.MoveNext()) break;
            prefix.Add(rest.Current);
        }
        if (prefix.Count > threshold) exhausted = false;

        run.DecideTier(prefix, exhausted ? prefix.Count : (int?)null);

        using (var sink = new NdjsonSink(output))
        {
            sink.WriteLine(run.BuildHeader(exhausted ? prefix.Count : (int?)null));

            foreach (var modelObject in prefix)
            {
                run.Context.Options.Cancellation.ThrowIfCancellationRequested();
                run.WriteRecord(modelObject, sink, allowReflectionFallback: false, abort: null);
                run.ReportProgress();
            }

            while (!exhausted && rest.MoveNext())
            {
                run.Context.Options.Cancellation.ThrowIfCancellationRequested();
                run.WriteRecord(rest.Current, sink, allowReflectionFallback: false, abort: null);
                run.ReportProgress();
            }

            sink.Flush();
        }

        return new DumpResult(
            run.Written,
            run.Skipped,
            run.Context.AttributeTier,
            run.Context.Warnings,
            stopwatch.Elapsed);
    }

    private static JsonValue WarningsValue(IReadOnlyList<DumpWarning> warnings)
    {
        var array = new JsonArray();
        foreach (var warning in warnings)
        {
            var value = new JsonObject();
            value.Add("code", JsonValue.String(warning.Code));
            value.Add("message", JsonValue.String(warning.Message));
            if (warning.ObjectType is not null) value.Add("objectType", JsonValue.String(warning.ObjectType));
            if (warning.Guid is not null) value.Add("guid", JsonValue.String(warning.Guid));
            array.Add(value);
        }
        return array;
    }

    /// <summary>One run's moving parts, shared by both modes so they cannot drift apart.</summary>
    private sealed class Run
    {
        private readonly ExtractorRegistry _registry = ExtractorRegistry.CreateDefault();

        public Run(DumpOptions? options)
        {
            var effective = (options ?? DumpOptions.Default).Clone();
            Context = new DumpContext(effective, SessionInfo.TryCreate());
        }

        public DumpContext Context { get; }

        public int Written { get; private set; }

        public int Skipped { get; private set; }

        /// <summary>
        /// Picks T0 / T1 / T2 and, for T2, runs the report join before the first record is written.
        /// </summary>
        /// <param name="sample">
        /// A prefix of the input, used to decide which content types are present and to pick the
        /// objects the T2 join is cross-checked against.
        /// </param>
        /// <param name="known">
        /// The object count when it is known. Null means the input is longer than the threshold, so
        /// T2 is on the table even though the exact size is not known.
        /// </param>
        public void DecideTier(IReadOnlyList<ModelObject> sample, int? known)
        {
            var options = Context.Options;
            var wantsAttributes =
                options.TemplateAttributes != TemplateAttributeScope.None ||
                (options.TemplateAttributeNames is not null && options.TemplateAttributeNames.Count > 0);

            if (!wantsAttributes)
            {
                Context.AttributeTier = "T0";
                return;
            }

            Context.AttributeTier = "T1";

            var overThreshold = known is null || known.Value > options.ReportJoinThreshold;
            if (!overThreshold) return;

            var modelPath = Context.Session?.ModelPath;
            if (modelPath is null)
            {
                Context.WarnOnce("t2-no-model-path", "report-join-unavailable",
                    "The object count is above the report-join threshold, but there is no model path to write a " +
                    "template into. Reading attributes per object instead; this will be slow.");
                return;
            }

            var rows = BuildRowSpecs(sample);
            if (rows.Count == 0) return;

            var reader = new ReportJoinReader(Context);
            var join = reader.Run(modelPath, rows);
            if (join is null) return;   // every failure path already warned; T1 stands

            Context.ReportJoin = join;
            Context.AttributeTier = "T2";

            WarnIfNumberingStale(rows);

            var byContentType = new Dictionary<string, ReportRowSpec>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows) byContentType[row.ContentType] = row;
            reader.VerifySample(join, Sample(sample, ReportJoinReader.SampleSize), byContentType);
        }

        /// <summary>
        /// One row per content type present in the input, carrying only the attributes the catalog
        /// declares for that type — an attribute that does not apply is left out of the row rather
        /// than failing the report.
        /// </summary>
        private IReadOnlyList<ReportRowSpec> BuildRowSpecs(IReadOnlyList<ModelObject> sample)
        {
            var rows = new List<ReportRowSpec>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var modelObject in sample)
            {
                var contentType = TeklaContentTypes.For(modelObject);
                if (contentType is null || !seen.Add(contentType)) continue;

                var plan = Context.PlanFor(contentType);
                if (plan.Definitions.Count > 0)
                    rows.Add(new ReportRowSpec(contentType, plan.Definitions));
            }

            return rows;
        }

        /// <summary>
        /// Position and mark attributes are EMPTY until numbering has run, and there is no API to
        /// run it. The header already stamps the fact; this says it out loud when the run actually
        /// asked for one of those attributes, which is when it matters.
        /// </summary>
        private void WarnIfNumberingStale(IReadOnlyList<ReportRowSpec> rows)
        {
            if (Context.Session?.NumberingUpToDate != false) return;

            foreach (var row in rows)
            {
                foreach (var attribute in row.Attributes)
                {
                    if (attribute.Name.IndexOf("POS", StringComparison.OrdinalIgnoreCase) < 0 &&
                        attribute.Name.IndexOf("MARK", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    Context.WarnOnce("numbering-stale", "numbering-not-up-to-date",
                        "Numbering is not up to date, so position and mark attributes such as " +
                        attribute.Name + " are empty or stale. Run numbering in Tekla and dump again.");
                    return;
                }
            }
        }

        /// <summary>Evenly spread rather than the first N: the first N are all one type.</summary>
        private static IReadOnlyList<ModelObject> Sample(IReadOnlyList<ModelObject> source, int size)
        {
            if (source.Count <= size) return source;

            var step = source.Count / size;
            var sample = new List<ModelObject>(size);
            for (var i = 0; i < source.Count && sample.Count < size; i += step)
                sample.Add(source[i]);
            return sample;
        }

        /// <summary>
        /// Writes one record. An extractor that throws costs that record and nothing else — the
        /// warning and <see cref="DumpResult.ObjectsSkipped"/> are what keep the loss visible.
        /// </summary>
        public void WriteRecord(ModelObject source, IDumpSink sink, bool allowReflectionFallback, Action? abort)
        {
            try
            {
                sink.BeginRecord();
                sink.Write("objectType", JsonValue.String(source.GetType().Name));

                var guid = Context.GuidOf(source);
                if (guid is not null) sink.Write("guid", JsonValue.String(guid));

                var extractor = _registry.Resolve(source.GetType());
                if (extractor is not null)
                {
                    extractor.Write(source, sink, Context);
                }
                else if (allowReflectionFallback)
                {
                    new ReflectionFallback(Context).Write(source, sink);
                }
                else
                {
                    // Cannot happen with the shipped registry (ModelObjectExtractor is the floor),
                    // but a caller-registered partial registry could get here.
                    Context.Warn("no-extractor",
                        "No extractor is registered for " + source.GetType().Name + " and reflection is off in bulk mode.",
                        source);
                }

                // Section order is fixed: create, derived, userProperties, templateAttributes,
                // componentAttributes — the order the schema README documents, and part of what
                // makes two runs of the same dump diff clean.
                Context.WriteAttributes(source, sink);

                if (source is BaseComponent component)
                    ComponentExtractor.WriteComponentAttributes(component, sink, Context);

                sink.EndRecord();
                Written++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                abort?.Invoke();
                Skipped++;
                Context.Warn("object-skipped", "Skipped: " + ex.Message, source);
            }
        }

        public void ReportProgress()
        {
            var progress = Context.Options.Progress;
            if (progress is null) return;
            if ((Written + Skipped) % ProgressInterval != 0) return;
            progress.Report(new DumpProgress(Written, Skipped, "writing"));
        }

        /// <summary>
        /// The header both modes share: what this is, what produced it, and every session fact that
        /// silently changes what the numbers mean.
        /// </summary>
        public JsonValue BuildHeader(int? objectCount)
        {
            var session = Context.Session;
            var options = Context.Options;
            var header = new JsonObject();

            header.Add("schemaVersion", JsonValue.String(SchemaVersion.Current));
            header.Add("domain", JsonValue.String(SchemaVersion.ModelDomain));
            header.Add("tool", JsonValue.String("tekla-dump " + ToolVersion));
            header.Add("generatedAt", JsonValue.String(
                DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture)));

            AddIfPresent(header, "teklaVersion", session?.TeklaVersion);
            AddIfPresent(header, "buildNumber", session?.BuildNumber);
            AddIfPresent(header, "environment", session?.Environment);
            AddIfPresent(header, "role", session?.Role);
            AddIfPresent(header, "modelName", session?.ModelName);

            // Off by default: an inspect file is meant to be pasted into a chat, and a customer's
            // folder path and Tekla user name do not belong in one.
            if (options.IncludeSessionDetails)
            {
                AddIfPresent(header, "modelPath", session?.ModelPath);
                AddIfPresent(header, "user", session?.User);
            }

            if (session?.CurrentPhase is int phase) header.Add("currentPhase", JsonValue.Number(phase));
            if (session?.SharedModel is bool shared) header.Add("sharedModel", JsonValue.Bool(shared));

            var workPlane = session?.WorkPlane;
            if (workPlane is not null)
            {
                header.Add("workPlane", JsonValue.String(workPlane.Name));
                if (workPlane.Origin is not null)
                {
                    header.Add("workPlaneOrigin",
                        Values.ValueCoercion.Point3(workPlane.Origin.X, workPlane.Origin.Y, workPlane.Origin.Z));
                }
                if (workPlane.Axes is not null)
                {
                    var axes = new JsonArray();
                    foreach (var axis in workPlane.Axes)
                        axes.Add(Values.ValueCoercion.Point3(axis.X, axis.Y, axis.Z));
                    header.Add("workPlaneAxes", axes);
                }
            }

            if (session?.NumberingUpToDate is bool numbering)
                header.Add("numberingUpToDate", JsonValue.Bool(numbering));

            header.Add("unitPolicy", JsonValue.String(options.Units == UnitPolicy.Normalized ? "normalized" : "native"));
            if (options.Units == UnitPolicy.Normalized)
            {
                var units = new JsonObject();
                foreach (var unit in Units.NormalizedUnits) units.Add(unit.Key, JsonValue.String(unit.Value));
                header.Add("units", units);
            }

            header.Add("attributeTier", JsonValue.String(Context.AttributeTier));
            if (Context.RequestedAttributeNames.Count > 0)
            {
                var names = new JsonArray();
                foreach (var name in Context.RequestedAttributeNames) names.Add(JsonValue.String(name));
                header.Add("attributeSet", names);
            }

            if (objectCount.HasValue) header.Add("objectCount", JsonValue.Number(objectCount.Value));

            return header;
        }

        private static void AddIfPresent(JsonObject header, string key, string? value)
        {
            if (!string.IsNullOrEmpty(value)) header.Add(key, JsonValue.String(value!));
        }

        /// <summary>
        /// The package version, read from the assembly rather than hardcoded so a release cannot
        /// stamp a stale number.
        /// </summary>
        private static string ToolVersion
        {
            get
            {
                try
                {
                    var assembly = typeof(DumpWriter).Assembly;
                    var informational = assembly
                        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
                    if (informational.Length > 0)
                    {
                        var version =
                            ((System.Reflection.AssemblyInformationalVersionAttribute)informational[0])
                            .InformationalVersion;
                        // GitVersion appends "+<sha>"; the sha is noise in a header field a human reads.
                        var plus = version.IndexOf('+');
                        return plus > 0 ? version.Substring(0, plus) : version;
                    }

                    return assembly.GetName().Version?.ToString() ?? "0.0.0";
                }
                catch (Exception)
                {
                    return "0.0.0";
                }
            }
        }

        /// <summary>Progress every N objects; per-object reporting costs more than the dump.</summary>
        private const int ProgressInterval = 500;
    }
}
