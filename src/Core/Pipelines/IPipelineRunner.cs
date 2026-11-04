namespace Magic.Pipelines;

/// <summary>
/// The pipelines the coordinator holds as one kind, whatever their state: type erasure over
/// <see cref="PipelineRunner{TState}"/>, so what registered can leave them all.
/// </summary>
internal interface IPipelineRunner
{
    void Remove(object pipeline);
}
