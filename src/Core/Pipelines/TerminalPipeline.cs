using Magic.CommandLine;
using Magic.Files;
using Magic.Runtime;

namespace Magic.Pipelines;

/// <summary>
/// Runs the lines typed into the terminal.
/// </summary>
internal sealed class TerminalPipeline(App app, IFileSystem files) : IPipeline<HostPump>
{
    public ValueTask<HostPump> RunAsync(HostPump state, CancellationToken cancel)
    {
        Cli.Pump(app, files);

        return ValueTask.FromResult(state);
    }
}
