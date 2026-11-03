using Magic.GemAPI;

namespace Magic.Pipelines;

/// <summary>
/// The pipelines Core holds as one kind, whatever their state: type erasure over <see cref="PipelineRunner{TState}"/>, so a
/// gem can leave them all and what was deferred runs in one order.
/// </summary>
internal interface IPipelineRunner
{
    void Remove(IGem gem);

    /// <summary>
    /// Runs the state deferred longest ago, if one still waits. Main thread.
    /// </summary>
    void RunNextDeferred(CancellationToken cancel);
}
