namespace Magic.Pipelines;

/// <summary>
/// A pipeline can't take this run because of what the caller is doing, not because anything is wrong with it: thrown from
/// its <see cref="IPipeline{TState}.RunAsync"/>, it goes to whoever ran the pipeline, and a gem's stays in its pipelines.
/// </summary>
public sealed class PipelineRefusedException : InvalidOperationException
{
    public PipelineRefusedException() { }

    public PipelineRefusedException(string message)
        : base(message) { }

    public PipelineRefusedException(string message, Exception innerException)
        : base(message, innerException) { }
}
