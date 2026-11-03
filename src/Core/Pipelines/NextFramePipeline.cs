using System.Diagnostics;
using Magic.GemAPI;
using Magic.Runtime;
using Magic.Worlds;

namespace Magic.Pipelines;

/// <summary>
/// The frame loop, a turn at a time: works out the clocks, runs a frame's pipelines in their order, with what was deferred
/// once the fixed steps are done, and sleeps until the next frame is due. A frame's pipeline may change the frame for those
/// after it; a fixed step's is its own.
/// </summary>
internal sealed class NextFramePipeline(App app, World world, Events events, PipelineCoordinator pipelines)
    : IPipeline<AppLoop>,
        IPipeline<CoreState>
{
    /// <summary>
    /// How long a fixed step is.
    /// </summary>
    private const double FixedStepSec = 1.0 / 60;

    private const int MaxFixedSteps = 4;

    /// <summary>
    /// How long a frame is, as nothing sets the pace yet.
    /// </summary>
    private const double FrameSec = 1.0 / 60;

    private readonly Stopwatch _clock = new();
    private readonly PipelineRunner<CoreState> _core = pipelines.Get<CoreState>();
    private readonly PipelineRunner<ServicePump> _servicePump = pipelines.Get<ServicePump>();
    private readonly PipelineRunner<BeginUpdate> _beginUpdate = pipelines.Get<BeginUpdate>();
    private readonly PipelineRunner<Update> _update = pipelines.Get<Update>();
    private readonly PipelineRunner<FixedUpdate> _fixedUpdate = pipelines.Get<FixedUpdate>();
    private readonly PipelineRunner<LateUpdate> _lateUpdate = pipelines.Get<LateUpdate>();
    private readonly PipelineRunner<Render> _render = pipelines.Get<Render>();
    private readonly PipelineRunner<EndUpdate> _endUpdate = pipelines.Get<EndUpdate>();

    public ValueTask<AppLoop> RunAsync(AppLoop state, CancellationToken cancel)
    {
        // Time begins with the first frame, not with what was loaded before it.
        _clock.Start();
        CoreState core = default;
        while (app.IsRunning && !app.HasFailed)
        {
            core = _core.Run(core, cancel);
        }

        return ValueTask.FromResult(state);
    }

    public ValueTask<CoreState> RunAsync(CoreState state, CancellationToken cancel)
    {
        state = GetClocks(state);
        state = state with { Frame = RunFrame(state, cancel) };

        return ValueTask.FromResult(state with { NextFrameSec = SleepUntilDue(state.NextFrameSec) });
    }

    /// <summary>
    /// The state of the frame after <paramref name="was"/>, at <paramref name="nowSec"/>.
    /// </summary>
    private static CoreState GetNext(CoreState was, double nowSec, bool isPaused, float timeScale)
    {
        double deltaSec = nowSec - was.LastSec;
        double simDeltaSec = isPaused ? 0 : deltaSec * timeScale;

        // Fixed steps catch the simulation up with the frame, a step at a time, at most MaxFixedSteps of them.
        double owedSec = was.FixedOwedSec + simDeltaSec;
        int fixedSteps = Math.Min((int)(owedSec / FixedStepSec), MaxFixedSteps);
        owedSec -= fixedSteps * FixedStepSec;

        return was with
        {
            Frame = new()
            {
                Index = was.NextIndex,
                AppSec = nowSec,
                AppDeltaSec = deltaSec,
                SimSec = was.SimSec + simDeltaSec,
                SimDeltaSec = simDeltaSec,
                IsPaused = isPaused,
            },
            FixedSteps = fixedSteps,
            NextIndex = was.NextIndex + 1,
            LastSec = nowSec,
            SimSec = was.SimSec + simDeltaSec,
            FixedSec = was.FixedSec + (was.FixedSteps * FixedStepSec),
            FixedOwedSec = owedSec >= FixedStepSec ? 0 : owedSec,
        };
    }

    private CoreState GetClocks(CoreState state)
    {
        // While the world loads, its time runs as it is, as the gems may need steps to get ready.
        bool isPaused = app.IsPaused || (world.IsPaused && !world.IsLoading);
        float timeScale = world.IsLoading ? 1 : world.TimeScale;

        return GetNext(state, _clock.Elapsed.TotalSeconds, isPaused, timeScale);
    }

    private Frame RunFrame(CoreState state, CancellationToken cancel)
    {
        _servicePump.Run(default, cancel);
        Frame frame = _beginUpdate.Run(new BeginUpdate(state.Frame with { Events = events.Take() }), cancel).Frame;
        frame = _update.Run(new Update(frame), cancel).Frame;
        for (int step = 1; step <= state.FixedSteps; step++)
        {
            Frame fixedFrame = frame with
            {
                SimSec = state.FixedSec + (step * FixedStepSec),
                SimDeltaSec = FixedStepSec,
                Events = FrameEvents.None,
            };
            _fixedUpdate.Run(new FixedUpdate(fixedFrame), cancel);
        }

        pipelines.RunDeferred(cancel);
        frame = _lateUpdate.Run(new LateUpdate(frame), cancel).Frame;
        frame = _render.Run(new Render(frame), cancel).Frame;

        return _endUpdate.Run(new EndUpdate(frame), cancel).Frame;
    }

    /// <summary>
    /// Sleeps until the next frame is due: when it is due, as fixed steps of <see cref="FrameSec"/>, so a sleep the OS rounds
    /// up to its timer's tick is made up by the next one; a frame late by more than a frame starts the count again rather
    /// than run frames back to back.
    /// </summary>
    private double SleepUntilDue(double lastDueSec)
    {
        double dueSec = lastDueSec + FrameSec;
        double leftSec = dueSec - _clock.Elapsed.TotalSeconds;
        if (leftSec > 0)
            Thread.Sleep(TimeSpan.FromSeconds(leftSec));
        else if (leftSec < -FrameSec)
            dueSec = _clock.Elapsed.TotalSeconds;

        return dueSec;
    }
}
