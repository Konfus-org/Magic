using Magic.CommandLine;
using Magic.Threading;

namespace Magic.Pipes;

/// <summary>
/// Keeps the services going, before each frame and while none run: runs what was posted to the main thread, and the awaited
/// flows that stepped onto it, and executes the lines typed into the terminal.
/// </summary>
internal sealed class ServicePumpPipeline(Threads threads, Cli cli) : IPipeline<ServicePump>
{
    public ValueTask<ServicePump> RunAsync(ServicePump state, CancellationToken cancel)
    {
        threads.Pump(EngineThread.Main);
        cli.Pump();

        return ValueTask.FromResult(state);
    }
}
