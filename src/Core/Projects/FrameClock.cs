using System.Diagnostics;
using Magic.Events;

namespace Magic.Projects;

/// <summary>
/// The frame's clocks: real time, the simulation's, and how many fixed steps the simulation owes. It starts with the first
/// frame.
/// </summary>
internal sealed class FrameClock
{
    /// <summary>
    /// How long a fixed step is.
    /// </summary>
    private const double FixedStepSec = 1.0 / 60;

    private const int MaxFixedSteps = 4;

    private readonly Stopwatch _clock = new();
    private uint _index;
    private double _lastSec;
    private double _simSec;
    private double _fixedSec;
    private double _owedSec;

    /// <summary>
    /// The next frame's clocks, and how many fixed steps catch the simulation up with it, at most
    /// <see cref="MaxFixedSteps"/>; what is owed past that is dropped.
    /// </summary>
    public Frame Advance(bool isPaused, float timeScale, out int fixedSteps)
    {
        _clock.Start();
        double nowSec = _clock.Elapsed.TotalSeconds;
        double simDeltaSec = isPaused ? 0 : (nowSec - _lastSec) * timeScale;
        _simSec += simDeltaSec;
        _owedSec += simDeltaSec;
        fixedSteps = Math.Min((int)(_owedSec / FixedStepSec), MaxFixedSteps);
        _owedSec -= fixedSteps * FixedStepSec;
        if (_owedSec >= FixedStepSec)
            _owedSec = 0;

        Frame frame = new()
        {
            Index = _index++,
            AppSec = nowSec,
            AppDeltaSec = nowSec - _lastSec,
            SimSec = _simSec,
            SimDeltaSec = simDeltaSec,
            IsPaused = isPaused,
        };
        _lastSec = nowSec;

        return frame;
    }

    /// <summary>
    /// <paramref name="frame"/> as its fixed step number <paramref name="step"/>, from 1, sees it.
    /// </summary>
    public Frame GetFixedStep(in Frame frame, int step) =>
        frame with
        {
            SimSec = _fixedSec + (step * FixedStepSec),
            SimDeltaSec = FixedStepSec,
            Events = FrameEvents.None,
        };

    public void EndFixedSteps(int steps) => _fixedSec += steps * FixedStepSec;
}
