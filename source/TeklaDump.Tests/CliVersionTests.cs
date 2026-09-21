using System;
using System.Diagnostics;
using System.IO;
using TeklaDump.Cli;
using Xunit;

namespace TeklaDump.Tests;

/// <summary>
/// The <c>--version</c> flag: that it parses, that it is documented, and what its three lines say.
/// </summary>
/// <remarks>
/// The flag exists because a bug report has to name the build it came from, so the line that
/// matters most is the one naming the <c>TeklaDump.dll</c> that was actually loaded — the exe and
/// the DLL ship in two separate downloads and can drift apart in one folder.
///
/// What these tests do NOT cover: a mismatched pair. One process loads one TeklaDump.dll, so the
/// schema line can only ever agree with the assembly the suite itself compiled against. The
/// missing-DLL half is covered by running the exe with nothing beside it, which is also the only
/// way to reach <c>Main</c>'s branch and its exit code.
/// </remarks>
public class CliVersionTests
{
    [Fact]
    public void The_flag_parses_on_its_own()
    {
        var command = CommandLine.Parse(new[] { "--version" });

        Assert.Null(command.Error);
        Assert.True(command.Version);
        Assert.False(command.Help);
    }

    [Fact]
    public void The_flag_parses_after_a_command()
    {
        // "tekla-dump bulk --version" answers about the tool, not about the run it did not do.
        var command = CommandLine.Parse(new[] { "bulk", "--version" });

        Assert.Null(command.Error);
        Assert.Equal("bulk", command.Command);
        Assert.True(command.Version);
    }

    [Fact]
    public void The_usage_text_documents_the_flag()
    {
        // A flag --help does not mention is a flag nobody runs.
        Assert.Contains("--version", CommandLine.Usage);
    }

    [Fact]
    public void The_version_text_names_the_exe_the_library_and_the_schema()
    {
        var text = Program.VersionText();
        var lines = text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(3, lines.Length);
        Assert.StartsWith("tekla-dump  ", lines[0]);
        Assert.StartsWith("library     ", lines[1]);
        Assert.Contains("TeklaDump.dll", lines[1]);
        Assert.Equal("schema      " + SchemaVersion.Current, lines[2]);

        // "<unknown>" means an assembly went out without its informational version — which is
        // exactly the state that makes the answer useless to whoever asked for it.
        Assert.DoesNotContain("<unknown>", text);
    }

    [Fact]
    public void Reports_a_missing_library_rather_than_failing()
    {
        // The exe and the DLL travel in separate zips, so "the DLL is not there" is a real outcome
        // — and the one a user hits before anything else works. --version has to survive it,
        // because it is what they will be asked to run.
        using var tree = new TempTree();

        var exe = Path.Combine(tree.Root, "tekla-dump.exe");
        var source = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tekla-dump.exe");
        Assert.True(File.Exists(source), source + " is missing; the CLI project reference did not copy it.");
        File.Copy(source, exe);

        var result = Run(exe, "--version");

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Error.Length == 0, "nothing belongs on stderr: " + result.Error);
        Assert.StartsWith("tekla-dump  ", result.Output);
        Assert.Contains("could not load TeklaDump.dll", result.Output);
    }

    private static (int ExitCode, string Output, string Error) Run(string exe, string arguments)
    {
        var startInfo = new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exe),
        };

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);

        // Read before waiting: a process blocked on a full pipe never exits, and a test that hangs
        // is worse than one that fails.
        var output = process!.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), exe + " did not exit within 30 seconds.");

        return (process.ExitCode, output, error);
    }
}
