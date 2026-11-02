using System.Diagnostics;
using Magic.Runtime;

namespace Magic.Pipelines;

/// <summary>
/// Works out this frame's clocks and how many fixed steps it owes, first in the core pipeline.
/// </summary>
internal sealed class ClockPipeline(App app, Stopwatch clock) : IPipeline<CoreState>
{
    /// <summary>
    /// How long a fixed step is.
    /// </summary>
    internal const double FixedStepSec = 1.0 / 60;

    private const int MaxFixedSteps = 4;

    public ValueTask<CoreState> RunAsync(CoreState state, CancellationToken cancel) =>
        ValueTask.FromResult(GetNext(state, clock.Elapsed.TotalSeconds, app.IsPaused));

    /// <summary>
    /// The state of the frame after <paramref name="was"/>, at <paramref name="nowSec"/>.
    /// </summary>
    private static CoreState GetNext(CoreState was, double nowSec, bool isPaused)
    {
        double deltaSec = nowSec - was.LastSec;
        double? pausedAtSec = isPaused ? was.PausedAtSec ?? nowSec : null;
        double? pausedDeltaSec = isPaused ? was.PausedDeltaSec ?? deltaSec : null;

        // Fixed steps catch the simulation up with the frame, a step at a time, at most MaxFixedSteps of them.
        double owedSec = was.FixedOwedSec + deltaSec;
        int fixedSteps = Math.Min((int)(owedSec / FixedStepSec), MaxFixedSteps);
        owedSec -= fixedSteps * FixedStepSec;

        return was with
        {
            Frame = new()
            {
                Index = was.NextIndex,
                AppSec = nowSec,
                AppDeltaSec = deltaSec,
                SimSec = pausedAtSec ?? nowSec,
                SimDeltaSec = pausedDeltaSec ?? deltaSec,
            },
            FixedSteps = fixedSteps,
            NextIndex = was.NextIndex + 1,
            LastSec = nowSec,
            PausedAtSec = pausedAtSec,
            PausedDeltaSec = pausedDeltaSec,
            FixedSec = was.FixedSec + (was.FixedSteps * FixedStepSec),
            FixedOwedSec = owedSec >= FixedStepSec ? 0 : owedSec,
        };
    }
}
