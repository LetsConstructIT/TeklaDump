using System;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace TeklaDump.Cli;

/// <summary>Entry point. Nothing here may touch a Tekla type — see <see cref="Main"/>.</summary>
internal static class Program
{
    /// <summary>Exit codes, as documented in <c>--help</c> and in the README.</summary>
    public const int Ok = 0;
    public const int UnexpectedError = 1;
    public const int NoSession = 2;
    public const int BadArguments = 3;
    public const int CompletedWithWarnings = 4;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Main(string[] args)
    {
        var command = CommandLine.Parse(args);

        if (command.Error is not null)
        {
            Console.Error.WriteLine(command.Error);
            return BadArguments;
        }

        if (command.Version)
        {
            Console.Write(VersionText());
            return Ok;
        }

        if (command.Help || command.Command.Length == 0)
        {
            Console.WriteLine(CommandLine.Usage);
            return command.Help ? Ok : BadArguments;
        }

        // The resolver must be installed before the first Tekla type is TOUCHED, and the JIT
        // resolves the types a method mentions when it compiles that method — so the real work
        // lives in a separate, non-inlined method that this one calls only after Install.
        TeklaAssemblyResolver.Install(command.TeklaBin);

        try
        {
            return Commands.Run(command);
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C during a bulk run: the file on disk is a valid truncated NDJSON, because every
            // completed line is independently parseable.
            Console.Error.WriteLine("Cancelled. The output written so far is valid NDJSON.");
            return CompletedWithWarnings;
        }
        catch (Exception ex) when (IsOpenApiBindingFailure(ex))
        {
            // Not an "unexpected error": the Open API simply could not be found, which is a
            // configuration answer with its own exit code and its own fix.
            Console.Error.WriteLine("Could not load the Tekla Open API: " + ex.Message);
            Console.Error.WriteLine(
                "Start Tekla Structures, or pass --tekla-bin <dir> pointing at an installation's bin folder. " +
                "Run 'tekla-dump doctor' for the full picture.");
            return NoSession;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("tekla-dump failed: " + ex.Message);
            Console.Error.WriteLine("Run 'tekla-dump doctor' and include its output in a bug report.");
            return UnexpectedError;
        }
    }

    /// <summary>
    /// What a bug report should open with: which exe, which <c>TeklaDump.dll</c> it actually
    /// loaded, and which schema that DLL writes.
    /// </summary>
    /// <remarks>
    /// The exe and the DLL ship in two separate downloads — the CLI zip and the macro bundle — so
    /// a mismatched pair sitting in one folder is a real failure mode, and these three lines are
    /// where it becomes visible. Printed before the assembly resolver is installed, because it has
    /// to work on a machine with no Tekla on it at all.
    /// </remarks>
    private static string VersionText()
    {
        var text = "tekla-dump  " + InformationalVersion(typeof(Program).Assembly) + "\n";

        try
        {
            return text + LibraryLines();
        }
        catch (Exception ex)
        {
            // TeklaDump.dll missing or unloadable IS the answer to "why does nothing work", so it
            // is reported rather than thrown — the same shape as doctor's session half.
            return text + "library     <could not load TeklaDump.dll: " + ex.Message + ">\n";
        }
    }

    /// <summary>The half that needs TeklaDump.dll loaded. Split out so its failure is reportable.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string LibraryLines()
    {
        var assembly = typeof(SchemaVersion).Assembly;
        var location = assembly.Location;

        // GetRawConstantValue reads the LOADED assembly's metadata. SchemaVersion.Current is a
        // const, so naming it directly would bake the exe's compile-time value into the exe and
        // report the wrong schema for exactly the mismatched pair this output exists to expose.
        var schema = typeof(SchemaVersion)
            .GetField("Current", BindingFlags.Public | BindingFlags.Static)
            ?.GetRawConstantValue() as string;

        return "library     " + InformationalVersion(assembly) +
               "  " + (string.IsNullOrEmpty(location) ? "<unknown location>" : location) + "\n" +
               "schema      " + (schema ?? "<unknown>") + "\n";
    }

    /// <summary>
    /// The informational version, sha and all. <c>DumpWriter</c> strips the <c>+&lt;sha&gt;</c> for
    /// the header field a human reads; here it is the point, because it names the exact build.
    /// </summary>
    private static string InformationalVersion(Assembly assembly)
    {
        var attributes = assembly.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false);
        if (attributes.Length > 0)
            return ((AssemblyInformationalVersionAttribute)attributes[0]).InformationalVersion;

        return assembly.GetName().Version?.ToString() ?? "<unknown>";
    }

    /// <summary>
    /// Whether the failure is "the Open API is not on this machine" rather than a real bug.
    /// </summary>
    /// <remarks>
    /// The exception surfaces as a FileNotFoundException, or wrapped in a TypeInitializationException
    /// when the first touch happened inside a static constructor, so both are unwrapped. Matching on
    /// the assembly name keeps an unrelated missing file from being reported as a Tekla problem.
    /// </remarks>
    private static bool IsOpenApiBindingFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is System.IO.FileNotFoundException or System.IO.FileLoadException or BadImageFormatException)
            {
                if (current.Message.IndexOf("Tekla.", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
        }

        return false;
    }
}
