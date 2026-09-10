using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Tekla.Structures.Model;
using TeklaDump.Attributes;
using TeklaDump.Session;

namespace TeklaDump.Cli;

/// <summary>The four commands. Everything Tekla-touching is here, behind the assembly resolver.</summary>
internal static class Commands
{
    public static int Run(CommandLine command)
    {
        switch (command.Command.ToLowerInvariant())
        {
            case "inspect": return Inspect(command);
            case "bulk": return Bulk(command);
            case "attrs": return Attrs(command);
            case "doctor": return Doctor(command);
            default:
                Console.Error.WriteLine("Unknown command '" + command.Command + "'. Run tekla-dump --help.");
                return Program.BadArguments;
        }
    }

    private static int Inspect(CommandLine command)
    {
        var collector = new TeklaObjectsCollector();
        if (!RequireSession(collector)) return Program.NoSession;

        var objects = Collect(collector, command, out var error, defaultToSelection: true);
        if (error is not null)
        {
            Console.Error.WriteLine(error);
            return Program.BadArguments;
        }

        var options = BuildOptions(command, sessionDetailsDefault: false);

        string text;
        using (GlobalWorkPlane.Enter(collector.Model, out var notice))
        {
            Notice(command, notice);
            text = DumpWriter.Inspect(objects, options).ToIndentedString();
        }

        WriteOutput(command, text);

        // A dump too big to paste is the most common way inspect mode disappoints, and the size is
        // knowable the moment it is written.
        var bytes = Encoding.UTF8.GetByteCount(text);
        if (bytes > 200 * 1024)
        {
            Notice(command,
                "This dump is " + (bytes / 1024) + " KB, which is probably too large to paste into a chat. " +
                "Try --no-derived, --attrs-scope none, or fewer objects.");
        }

        return Program.Ok;
    }

    private static int Bulk(CommandLine command)
    {
        var collector = new TeklaObjectsCollector();
        if (!RequireSession(collector)) return Program.NoSession;

        var objects = Collect(collector, command, out var error, defaultToSelection: false);
        if (error is not null)
        {
            Console.Error.WriteLine(error);
            return Program.BadArguments;
        }

        var options = BuildOptions(command, sessionDetailsDefault: true);
        if (command.Progress && !command.Quiet)
        {
            options.Progress = new Progress<DumpProgress>(progress =>
                Console.Error.WriteLine(progress.Phase + ": " + progress.ObjectsWritten + " written, " +
                                        progress.ObjectsSkipped + " skipped"));
        }

        DumpResult result;
        using (GlobalWorkPlane.Enter(collector.Model, out var notice))
        {
            Notice(command, notice);

            using var output = OpenOutput(command);
            result = DumpWriter.Bulk(objects, output, options);
        }

        if (!command.Quiet)
        {
            Console.Error.WriteLine(
                result.ObjectsWritten + " objects written, " + result.ObjectsSkipped + " skipped, tier " +
                result.AttributeTier + ", " + result.Elapsed.TotalSeconds.ToString("0.0") + "s");
        }

        ReportWarnings(command, result.Warnings);

        return result.ObjectsSkipped > 0 || result.Warnings.Count > 0
            ? Program.CompletedWithWarnings
            : Program.Ok;
    }

    /// <summary>
    /// What the running environment's catalog offers — the answer to "what can I pass to --attrs".
    /// </summary>
    private static int Attrs(CommandLine command)
    {
        var collector = new TeklaObjectsCollector();
        if (!RequireSession(collector)) return Program.NoSession;

        var session = SessionInfo.FromModel(collector.Model);
        var catalog = TemplateAttributeCatalogProvider.Load(session.ModelPath);

        if (catalog.IsEmpty)
        {
            Console.Error.WriteLine(
                "No contentattributes*.lst was found. Searched " + catalog.SearchedDirectories.Count +
                " folder(s) under the model, XS_PROJECT, XS_FIRM, XS_SYSTEM, XS_TPLED_INI and the " +
                "Template Editor settings.");
            return Program.CompletedWithWarnings;
        }

        var contentTypes = command.For is not null
            ? new[] { command.For }
            : new[] { "PART", "ASSEMBLY", "BOLT", "WELD", "REBAR", "CONNECTION" };

        var builder = new StringBuilder();
        foreach (var contentType in contentTypes)
        {
            var definitions = catalog.ForContentType(contentType);
            if (definitions.Count == 0) continue;

            builder.Append("# ").Append(contentType).Append(" (").Append(definitions.Count).Append(")").Append('\n');
            foreach (var definition in definitions.OrderBy(d => d.Name, StringComparer.Ordinal))
            {
                builder.Append(definition.Name)
                       .Append('\t').Append(definition.ValueType)
                       // The cost tier, which is the thing worth knowing before asking for it.
                       .Append('\t').Append(definition.IsDirect
                            ? "direct"
                            : definition.IsRelatedObject ? "related-object" : "constituent")
                       .Append('\n');
            }
            builder.Append('\n');
        }

        WriteOutput(command, builder.ToString());
        return Program.Ok;
    }

    /// <summary>
    /// The output an issue report should start with: what session, what version, how the Open API
    /// was found, and the two facts that silently change what a dump means.
    /// </summary>
    /// <remarks>
    /// Doctor is the one command that must survive the Open API not loading at all — that failure
    /// is exactly what someone runs it to diagnose. So the Tekla-touching half is a separate,
    /// non-inlined method behind a catch, and everything knowable without Tekla is printed first.
    /// </remarks>
    private static int Doctor(CommandLine command)
    {
        var builder = new StringBuilder();
        builder.Append("tekla-dump doctor").Append('\n');

        var teklaBin = TeklaAssemblyResolver.RunningTeklaBinDirectory();
        builder.Append("running Tekla bin:    ").Append(teklaBin ?? "<none found>").Append('\n');
        builder.Append("--tekla-bin override: ").Append(command.TeklaBin ?? "<not set>").Append('\n');

        int exitCode;
        try
        {
            exitCode = DoctorSession(builder);
        }
        catch (Exception ex)
        {
            builder.Append("Open API:             COULD NOT LOAD").Append('\n');
            builder.Append("  ").Append(ex.Message).Append('\n').Append('\n');
            builder.Append(
                "The Tekla Open API assemblies could not be resolved. They normally come from the GAC," + "\n" +
                "where a Tekla installation registers them; failing that, from the bin folder of a" + "\n" +
                "running TeklaStructures.exe. Start Tekla, or point at an installation explicitly:" + "\n" +
                "  tekla-dump doctor --tekla-bin \"C:\\TeklaStructures\\2026.0\\bin\"" + "\n");
            exitCode = Program.NoSession;
        }

        Console.Write(builder.ToString());
        return exitCode;
    }

    /// <summary>The half that needs the Open API loaded. Split out so its failure is reportable.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int DoctorSession(StringBuilder builder)
    {
        try
        {
            var location = typeof(Model).Assembly.Location;
            builder.Append("Open API loaded from: ")
                   .Append(string.IsNullOrEmpty(location) ? "<GAC or in-memory>" : location).Append('\n');
        }
        catch (Exception ex)
        {
            builder.Append("Open API loaded from: <failed: ").Append(ex.Message).Append(">").Append('\n');
        }

        var collector = new TeklaObjectsCollector();
        var connected = collector.IsConnected;
        builder.Append("connected:            ").Append(connected).Append('\n');

        if (!connected)
        {
            builder.Append('\n').Append(NoSessionMessage).Append('\n');
            return Program.NoSession;
        }

        var session = SessionInfo.FromModel(collector.Model);
        builder.Append("Tekla version:        ").Append(session.TeklaVersion ?? "<unknown>").Append('\n');
        builder.Append("build:                ").Append(session.BuildNumber ?? "<unknown>").Append('\n');
        builder.Append("environment:          ").Append(session.Environment ?? "<no advanced option answered>").Append('\n');
        builder.Append("role:                 ").Append(session.Role ?? "<no advanced option answered>").Append('\n');
        builder.Append("model:                ").Append(session.ModelName ?? "<unknown>").Append('\n');
        builder.Append("model path:           ").Append(session.ModelPath ?? "<unknown>").Append('\n');
        builder.Append("shared model:         ").Append(session.SharedModel?.ToString() ?? "<unknown>").Append('\n');
        builder.Append("current phase:        ").Append(session.CurrentPhase?.ToString() ?? "<unknown>").Append('\n');

        // The two facts that change what a dump MEANS rather than whether it works.
        builder.Append("work plane:           ").Append(session.WorkPlane.Name)
               .Append(session.WorkPlane.IsGlobal ? "" : "   <- local plane; inspect and bulk reset this")
               .Append('\n');
        builder.Append("numbering up to date: ").Append(session.NumberingUpToDate?.ToString() ?? "<unknown>")
               .Append(session.NumberingUpToDate == false ? "   <- position and mark attributes are empty or stale" : "")
               .Append('\n');

        var catalog = TemplateAttributeCatalogProvider.Load(session.ModelPath);
        builder.Append("attribute catalog:    ")
               .Append(catalog.IsEmpty
                    ? "EMPTY (searched " + catalog.SearchedDirectories.Count + " folders)"
                    : catalog.AttributeCount + " attributes from " + catalog.SourceFiles.Count + " file(s)")
               .Append('\n');

        return Program.Ok;
    }

    private static IEnumerable<ModelObject> Collect(
        TeklaObjectsCollector collector, CommandLine command, out string? error, bool defaultToSelection)
    {
        error = null;
        IEnumerable<ModelObject> objects;

        if (command.Selected)
        {
            objects = collector.GetSelectedObjects();
        }
        else if (command.Guids.Count > 0)
        {
            var unresolved = new List<string>();
            objects = collector.GetObjectsByGuids(command.Guids, unresolved).ToList();
            foreach (var guid in unresolved)
                Console.Error.WriteLine("No object with GUID " + guid + ".");
        }
        else if (command.Types.Count > 0)
        {
            var types = new List<Type>();
            foreach (var name in command.Types)
            {
                var type = TeklaObjectsCollector.ResolveType(name);
                if (type is null)
                {
                    error = "Unknown type '" + name + "'. Use a CLR type name such as Beam, ContourPlate or BoltArray.";
                    return new ModelObject[0];
                }
                types.Add(type);
            }

            objects = collector.GetObjectsOfTypes(types.ToArray());
        }
        else if (command.Filter is not null)
        {
            objects = collector.GetObjectsByFilterName(command.Filter);
        }
        else if (command.All)
        {
            objects = collector.GetAllObjects();
        }
        else if (defaultToSelection)
        {
            objects = collector.GetSelectedObjects();
        }
        else
        {
            error = "Nothing to dump. Pass --all, --selected, --type, --guid or --filter.";
            return new ModelObject[0];
        }

        if (command.Phase.HasValue)
        {
            var phase = command.Phase.Value;
            objects = objects.Where(o => TeklaObjectsCollector.PhaseOf(o) == phase);
        }

        // Sorted by GUID so two runs of the same command diff clean. The library writes objects in
        // the order it is handed them and deliberately does no sorting of its own.
        return objects.OrderBy(GuidKey, StringComparer.Ordinal);
    }

    private static string GuidKey(ModelObject modelObject)
    {
        try { return modelObject.Identifier.GUID.ToString(); }
        catch (Exception) { return string.Empty; }
    }

    private static DumpOptions BuildOptions(CommandLine command, bool sessionDetailsDefault)
    {
        var options = DumpOptions.Default;

        options.IncludeSessionDetails = command.SessionDetails ?? sessionDetailsDefault;
        options.IncludeDerived = !command.NoDerived;

        if (command.Units == "native") options.Units = UnitPolicy.Native;

        options.Geometry = command.Geometry switch
        {
            "none" => GeometryDetail.None,
            "solids" => GeometryDetail.Solids,
            _ => GeometryDetail.Points,
        };

        options.TemplateAttributes = command.AttributeScope switch
        {
            "associated" => TemplateAttributeScope.Associated,
            "full" => TemplateAttributeScope.Full,
            _ => TemplateAttributeScope.None,
        };

        if (command.Attributes is not null && command.Attributes.Count > 0)
        {
            options.TemplateAttributeNames = command.Attributes;
            // Explicit names imply the user wants attributes; leaving the scope at None would read
            // nothing and look like the names were wrong.
            if (options.TemplateAttributes == TemplateAttributeScope.None)
                options.TemplateAttributes = TemplateAttributeScope.Associated;
        }

        if (command.ComponentAttributesFile is not null)
        {
            options.ComponentAttributeNames = ReadLines(command.ComponentAttributesFile);
            options.ComponentAttributes = ComponentAttributeMode.Explicit;
        }

        if (command.ReportJoinThreshold.HasValue) options.ReportJoinThreshold = command.ReportJoinThreshold.Value;
        if (command.MaxTemplateAttributes.HasValue) options.MaxTemplateAttributes = command.MaxTemplateAttributes.Value;

        return options;
    }

    private static IReadOnlyList<string> ReadLines(string path)
    {
        var names = new List<string>();
        foreach (var line in File.ReadAllLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#') continue;
            names.Add(trimmed);
        }
        return names;
    }

    private static bool RequireSession(TeklaObjectsCollector collector)
    {
        if (collector.IsConnected) return true;

        Console.Error.WriteLine(NoSessionMessage);
        return false;
    }

    /// <summary>The single most common user error, so it gets a real message, not a stack trace.</summary>
    private const string NoSessionMessage =
        "No running Tekla Structures session found. Start Tekla, open a model, and try again.";

    private static Stream OpenOutput(CommandLine command)
    {
        if (command.Output is null) return Console.OpenStandardOutput();
        return new FileStream(command.Output, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024);
    }

    private static void WriteOutput(CommandLine command, string text)
    {
        if (command.Output is null)
        {
            // stdout when -o is omitted, so the tool pipes into jq or a chunker. Progress and
            // notices go to stderr for the same reason.
            Console.Out.Write(text);
            Console.Out.Flush();
            return;
        }

        File.WriteAllBytes(
            command.Output,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text));

        if (!command.Quiet) Console.Error.WriteLine("Wrote " + command.Output);
    }

    private static void ReportWarnings(CommandLine command, IReadOnlyList<DumpWarning> warnings)
    {
        if (command.Quiet || warnings.Count == 0) return;

        // Capped: a warning per object on a 200k-object run is a denial of service, and the codes
        // repeat. The count tells the user there is more.
        const int maxShown = 20;
        foreach (var warning in warnings.Take(maxShown))
            Console.Error.WriteLine("warning: " + warning);

        if (warnings.Count > maxShown)
            Console.Error.WriteLine("... and " + (warnings.Count - maxShown) + " more warnings.");
    }

    private static void Notice(CommandLine command, string? notice)
    {
        if (notice is null || command.Quiet) return;
        Console.Error.WriteLine(notice);
    }
}
