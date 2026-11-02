using Magic.Threading;

namespace Magic.Pipelines;

/// <summary>
/// Runs what was posted to the main thread, and the awaited flows that stepped onto it.
/// </summary>
internal sealed class PostedWorkPipeline(Threads threads) : IPipeline<HostPump>
{
    public ValueTask<HostPump> RunAsync(HostPump state, CancellationToken cancel)
    {
        threads.Pump(EngineThread.Main);

        return ValueTask.FromResult(state);
    }
}
