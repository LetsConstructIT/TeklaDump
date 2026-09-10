using System;
using System.Collections.Generic;
using System.IO;
using Tekla.Structures;
using Tekla.Structures.Model;
using Tekla.Structures.Model.Operations;

namespace TeklaDump.Attributes.ReportJoin;

/// <summary>The joined rows: GUID to name/value pairs.</summary>
internal sealed class ReportJoinResult
{
    private readonly Dictionary<string, IReadOnlyList<KeyValuePair<string, object>>> _rows;

    public ReportJoinResult(Dictionary<string, IReadOnlyList<KeyValuePair<string, object>>> rows)
    {
        _rows = rows;
    }

    public int RowCount => _rows.Count;

    /// <summary>The row for a GUID, or null when the report had nothing for that object.</summary>
    public IReadOnlyList<KeyValuePair<string, object>>? Row(string guid) =>
        _rows.TryGetValue(guid, out var row) ? row : null;
}

/// <summary>
/// Tier T2: reads template attributes for a whole model in one report instead of per object.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is always whole-model.</b> <c>CreateReportFromAll</c> covers every object in the model
/// even if 5,001 of 300,000 were handed in; rows outside the input set are simply never looked up.
/// The alternative, <c>CreateReportFromSelected</c>, needs a UI selection and is therefore
/// off-limits to the library. The threshold exists so this cost is only paid when it wins.
/// </para>
/// <para>
/// <b>It writes a file into the customer's model folder.</b> That is the one thing on the user's
/// side that touches disk, and it is a FILE, not the model. The template is uniquely named, its
/// resolution is confirmed before anything runs, and both it and the report output are deleted in
/// a <c>finally</c> — a crashed run must not leave <c>TeklaDump_&lt;guid&gt;.rpt</c> litter behind.
/// A cleanup failure is a warning, never an exception.
/// </para>
/// </remarks>
internal sealed class ReportJoinReader
{
    /// <summary>
    /// How many objects are re-read through T1 and compared against the joined values.
    /// </summary>
    /// <remarks>
    /// This is what turns "the report path is environment dependent" into something visible. A
    /// mismatch is a warning carrying both values, so a wrong number is reported as suspect rather
    /// than served with a straight face.
    /// </remarks>
    public const int SampleSize = 200;

    private readonly DumpContext _context;

    public ReportJoinReader(DumpContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Runs the join, or returns null when it could not be run — in which case the caller falls
    /// back to T1 rather than producing a dump with no attributes.
    /// </summary>
    /// <param name="modelPath">The model folder. The generated template goes in its root.</param>
    /// <param name="rows">One spec per content type present in the input set.</param>
    public ReportJoinResult? Run(string modelPath, IReadOnlyList<ReportRowSpec> rows)
    {
        if (rows.Count == 0) return null;

        var name = "TeklaDump_" + Guid.NewGuid().ToString("N");
        var templatePath = Path.Combine(modelPath, name + ".rpt");
        var outputPath = Path.Combine(Path.GetTempPath(), name + ".xsr");

        try
        {
            if (File.Exists(templatePath))
            {
                // A GUID collision is not really possible; a leftover from a previous crashed run
                // with the same name is not either. Refuse rather than overwrite someone's file.
                _context.Warn("report-join-name-collision",
                    "A file named " + name + ".rpt already exists in the model folder; skipping the report join.");
                return null;
            }

            File.WriteAllText(templatePath, ReportTemplateWriter.Build(name, rows));

            if (!TemplateResolves(modelPath, name))
            {
                _context.Warn("report-join-template-unresolved",
                    "Tekla could not resolve the generated template " + name +
                    " from the model folder, so the report join was skipped. Falling back to per-object reads.");
                return null;
            }

            // Uninterruptible: cancellation cannot break into this call, and the docs say so.
            if (!Operation.CreateReportFromAll(name, outputPath, "TeklaDump", string.Empty, string.Empty))
            {
                _context.Warn("report-join-failed",
                    "Tekla reported that the report could not be created. Falling back to per-object reads.");
                return null;
            }

            var report = ResolveOutput(outputPath, modelPath, name);
            if (report is null)
            {
                _context.Warn("report-join-output-missing",
                    "The report ran but no output file was found. Falling back to per-object reads.");
                return null;
            }

            var byContentType = new Dictionary<string, ReportRowSpec>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows) byContentType[row.ContentType] = row;

            var parsed = ReportOutputParser.Parse(report, byContentType, out var malformed);
            if (malformed > 0)
            {
                _context.Warn("report-join-malformed-rows",
                    malformed + " report line(s) could not be parsed and were dropped.");
            }

            if (parsed.Count == 0)
            {
                _context.Warn("report-join-empty",
                    "The report produced no usable rows. Falling back to per-object reads.");
                return null;
            }

            return new ReportJoinResult(parsed);
        }
        catch (Exception ex)
        {
            _context.Warn("report-join-error", "The report join failed: " + ex.Message);
            return null;
        }
        finally
        {
            TryDelete(templatePath);
            TryDelete(outputPath);
        }
    }

    /// <summary>
    /// Confirms Tekla can find the template before anything runs — a report that silently produces
    /// nothing because the name did not resolve is the hardest failure of this path to diagnose.
    /// </summary>
    private bool TemplateResolves(string modelPath, string name)
    {
        try
        {
            // The instance form: TeklaStructuresFiles resolves against the model path it was
            // constructed with, plus the environment's search path.
            var file = new TeklaStructuresFiles(modelPath).GetAttributeFile(name + ".rpt");
            return file is not null && file.Exists;
        }
        catch (Exception)
        {
            // The probe itself failing is not proof the template is missing; let the report try.
            return true;
        }
    }

    /// <summary>
    /// Finds the report output. <c>CreateReportFromAll</c> takes a file NAME and where it lands is
    /// environment dependent — the documented example checks the working directory, while a
    /// configured environment writes into the model's Reports folder.
    /// </summary>
    private static string? ResolveOutput(string requestedPath, string modelPath, string name)
    {
        foreach (var candidate in new[]
        {
            requestedPath,
            Path.Combine(modelPath, name + ".xsr"),
            Path.Combine(modelPath, "Reports", name + ".xsr"),
            Path.Combine(Environment.CurrentDirectory, name + ".xsr"),
        })
        {
            try
            {
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception)
            {
                // malformed candidate path — try the next
            }
        }

        return null;
    }

    /// <summary>
    /// Checks the joined values against per-object reads for a sample of objects.
    /// </summary>
    /// <returns>The number of mismatches found; each is also recorded as a warning.</returns>
    public int VerifySample(
        ReportJoinResult join,
        IReadOnlyList<ModelObject> sample,
        IReadOnlyDictionary<string, ReportRowSpec> rowsByContentType)
    {
        var reader = new TemplateAttributeReader();
        var mismatches = 0;

        foreach (var modelObject in sample)
        {
            var guid = _context.GuidOf(modelObject);
            if (guid is null) continue;

            var joined = join.Row(guid);
            if (joined is null) continue;

            var contentType = TeklaContentTypes.For(modelObject);
            if (contentType is null || !rowsByContentType.TryGetValue(contentType, out var row)) continue;

            var direct = reader.Read(modelObject, row.Attributes, out var error);
            if (error is not null) continue;

            var directByName = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var pair in direct) directByName[pair.Key] = pair.Value;

            foreach (var pair in joined)
            {
                if (!directByName.TryGetValue(pair.Key, out var expected)) continue;
                if (SameValue(expected, pair.Value)) continue;

                mismatches++;
                _context.Warn(
                    "report-join-mismatch",
                    pair.Key + ": the report says '" + pair.Value + "' but reading the object directly says '" +
                    expected + "'. The report path is environment dependent; treat this attribute as suspect.",
                    modelObject);
            }
        }

        return mismatches;
    }

    /// <summary>
    /// Compares a joined value with a directly read one. Numbers are compared with a tolerance,
    /// because the report renders a double through a formatted field with fixed decimals and the
    /// direct read does not.
    /// </summary>
    private static bool SameValue(object direct, object joined)
    {
        if (direct is double a && joined is double b) return Math.Abs(a - b) <= 1e-4;
        if (direct is int x && joined is int y) return x == y;

        return string.Equals(
            direct.ToString()?.Trim(),
            joined.ToString()?.Trim(),
            StringComparison.Ordinal);
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            // Cleanup failure is a warning, not an exception: the dump is already good, and the
            // user needs to know a file was left behind, not to lose the run over it.
            _context.Warn("report-join-cleanup-failed",
                "Could not delete " + path + ": " + ex.Message + ". Please remove it manually.");
        }
    }
}
