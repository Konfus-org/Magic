using Magic.GemAPI;

namespace Magic.Pipelines;

/// <summary>
/// The pipelines Core holds as one kind, whatever their state: type erasure over <see cref="PipelineList{TState}"/>, so a gem can leave
/// them all.
/// </summary>
internal interface IPipelineList
{
    void Remove(IGem gem);
}
