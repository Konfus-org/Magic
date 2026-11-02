using Magic.GemAPI;

namespace Magic.Pipelines;

/// <summary>
/// Runs a frame's pipelines in their order. One may change the frame for those after it; a fixed step's is its own.
/// </summary>
internal sealed class FramePipeline(PipelineCoordinator pipelines) : IPipeline<CoreState>
{
    private readonly PipelineList<BeginUpdate> _beginUpdate = pipelines.Get<BeginUpdate>();
    private readonly PipelineList<Update> _update = pipelines.Get<Update>();
    private readonly PipelineList<FixedUpdate> _fixedUpdate = pipelines.Get<FixedUpdate>();
    private readonly PipelineList<LateUpdate> _lateUpdate = pipelines.Get<LateUpdate>();
    private readonly PipelineList<Render> _render = pipelines.Get<Render>();
    private readonly PipelineList<EndUpdate> _endUpdate = pipelines.Get<EndUpdate>();

    public ValueTask<CoreState> RunAsync(CoreState state, CancellationToken cancel)
    {
        Frame frame = _beginUpdate.Run(new BeginUpdate(state.Frame), cancel).Frame;
        frame = _update.Run(new Update(frame), cancel).Frame;
        for (int step = 1; step <= state.FixedSteps; step++)
        {
            Frame fixedFrame = frame with
            {
                SimSec = state.FixedSec + (step * ClockPipeline.FixedStepSec),
                SimDeltaSec = ClockPipeline.FixedStepSec,
                Events = FrameEvents.None,
            };
            _fixedUpdate.Run(new FixedUpdate(fixedFrame), cancel);
        }

        frame = _lateUpdate.Run(new LateUpdate(frame), cancel).Frame;
        frame = _render.Run(new Render(frame), cancel).Frame;
        frame = _endUpdate.Run(new EndUpdate(frame), cancel).Frame;

        return ValueTask.FromResult(state with { Frame = frame });
    }
}
