using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

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

    /// <summary>
    /// Signalled by Ctrl+C and by nothing else. The library polls it between records; see
    /// <see cref="RequestCancellation"/> for why the keypress is handled rather than fatal.
    /// </summary>
    private static readonly CancellationTokenSource Cancellation = new CancellationTokenSource();

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

        // Ctrl+C has to be handled, not fatal — see RequestCancellation. Installed before the
        // first Tekla call, because that call is one of the ones worth interrupting.
        Console.CancelKeyPress += OnCancelKeyPress;

        // The resolver must be installed before the first Tekla type is TOUCHED, and the JIT
        // resolves the types a method mentions when it compiles that method — so the real work
        // lives in a separate, non-inlined method that this one calls only after Install.
        TeklaAssemblyResolver.Install(command.TeklaBin);

        try
        {
            return Commands.Run(command, Cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // Bulk only: the cancellation check sits between records and the sink closes on a
            // complete line, so the file on disk is a valid truncated NDJSON. Inspect builds its
            // document before it writes anything, so there is nothing on disk to describe.
            Console.Error.WriteLine(
                string.Equals(command.Command, "bulk", StringComparison.OrdinalIgnoreCase)
                    ? "Cancelled. The output written so far is valid NDJSON."
                    : "Cancelled.");
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

    private static void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e) =>
        e.Cancel = RequestCancellation(Cancellation);

    /// <summary>
    /// What a Ctrl+C does. Returns whether the keypress was handled — false lets the runtime kill
    /// the process, which is the default behaviour and what the second press restores.
    /// </summary>
    /// <remarks>
    /// The first press has to be handled rather than fatal, because the CLI leaves two things
    /// behind when it is killed outright: the user's work plane, which it normalized to global and
    /// restores in a finally, and up to 64 KB of buffered NDJSON, which is lost mid-line and takes
    /// the "every completed line is parseable" promise with it. Cancelling unwinds both.
    ///
    /// The second press has to kill, because cancellation is polled between records and a long
    /// Tekla call — a report join, a 200k-object enumeration — reaches no check point for minutes.
    /// Swallowing every Ctrl+C would take the user's last way out of a run that looks hung.
    ///
    /// The message is the answer to a keypress rather than a notice about the run, so it ignores
    /// --quiet: someone who just pressed Ctrl+C is owed a reply.
    /// </remarks>
    internal static bool RequestCancellation(CancellationTokenSource source)
    {
        if (source.IsCancellationRequested) return false;

        source.Cancel();
        Console.Error.WriteLine("Cancelling. Press Ctrl+C again to stop immediately.");
        return true;
    }    /// <summary>
    /// What a bug report should open with: which exe, which <c>TeklaDump.dll</c> it actually
    /// loaded, and which schema that DLL writes.
    /// </summary>
    /// <remarks>
    /// The exe and the DLL ship in two separate downloads — the CLI zip and the macro bundle — so
    /// a mismatched pair sitting in one folder is a real failure mode, and these three lines are
    /// where it becomes visible. Printed before the assembly resolver is installed, because it has
    /// to work on a machine with no Tekla on it at all.
    ///
    /// Internal rather than private so the suite can assert the three lines directly; the
    /// degraded path, where TeklaDump.dll is not there at all, is covered by running the exe.
    /// </remarks>
    internal static string VersionText()
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
