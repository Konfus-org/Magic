using Magic.CommandLine;

namespace Magic.Pipelines;

/// <summary>
/// Executes the lines typed into the terminal.
/// </summary>
internal sealed class TerminalPipeline(Cli cli) : IPipeline<HostPump>
{
    public ValueTask<HostPump> RunAsync(HostPump state, CancellationToken cancel)
    {
        cli.Pump();

        return ValueTask.FromResult(state);
    }
}
