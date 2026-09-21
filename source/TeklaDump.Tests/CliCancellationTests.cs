using System.Threading;
using TeklaDump.Cli;
using Xunit;

namespace TeklaDump.Tests;

/// <summary>
/// Ctrl+C: what the two presses do, and that the token actually reaches the library.
/// </summary>
/// <remarks>
/// Delivering a real Ctrl+C to a child process needs console control events and a process group,
/// and there would be nothing to cancel without a live session — so the keypress itself is not
/// tested. What is tested is the decision it makes and the wiring it depends on, which are the two
/// halves that can silently rot: a handler that always swallows the signal takes away the user's
/// way out of a hung run, and a token that never reaches <c>DumpOptions</c> makes the whole
/// mechanism a no-op that still compiles.
/// </remarks>
public class CliCancellationTests
{
    [Fact]
    public void The_first_press_cancels_and_the_second_lets_the_process_die()
    {
        using var source = new CancellationTokenSource();

        // True means "handled", which is what stops the runtime killing the process mid-write.
        Assert.True(Program.RequestCancellation(source));
        Assert.True(source.IsCancellationRequested);

        // False means "not handled": the default behaviour comes back, and Ctrl+C kills. Without
        // this a run stuck in a long Tekla call — one that reaches no check point for minutes —
        // would have no way out at all.
        Assert.False(Program.RequestCancellation(source));
    }

    [Fact]
    public void The_options_a_run_is_built_with_carry_the_token()
    {
        using var source = new CancellationTokenSource();
        var command = CommandLine.Parse(new[] { "bulk", "--all" });

        var options = Commands.BuildOptions(command, sessionDetailsDefault: true, source.Token);

        // DumpOptions.Default carries default(CancellationToken), which can never be cancelled —
        // so this assertion is the difference between a wired mechanism and a no-op.
        Assert.True(options.Cancellation.CanBeCanceled);
        Assert.Equal(source.Token, options.Cancellation);

        source.Cancel();
        Assert.True(options.Cancellation.IsCancellationRequested);
    }
}
