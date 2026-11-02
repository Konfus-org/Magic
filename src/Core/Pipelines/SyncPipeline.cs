using System.Diagnostics;

namespace Magic.Pipelines;

/// <summary>
/// Sleeps until the next frame is due, last of Core's own in the core pipeline, as nothing sets the pace yet. Due
/// times are fixed steps of <see cref="FrameSec"/>, so a sleep the OS rounds up to its timer's tick is made up by the
/// next one; a frame late by more than a frame starts the count again rather than run frames back to back.
/// </summary>
internal sealed class SyncPipeline(Stopwatch clock) : IPipeline<CoreState>
{
    private const double FrameSec = 1.0 / 60;

    public ValueTask<CoreState> RunAsync(CoreState state, CancellationToken cancel)
    {
        double nextFrameSec = state.NextFrameSec + FrameSec;
        double leftSec = nextFrameSec - clock.Elapsed.TotalSeconds;
        if (leftSec > 0)
            Thread.Sleep(TimeSpan.FromSeconds(leftSec));
        else if (leftSec < -FrameSec)
            nextFrameSec = clock.Elapsed.TotalSeconds;

        return ValueTask.FromResult(state with { NextFrameSec = nextFrameSec });
    }
}
