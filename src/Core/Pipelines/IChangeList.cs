namespace Magic.Pipelines;

/// <summary>
/// A pipeline state that is pushed onto as a frame goes, so several pushes share one run: what
/// <see cref="PipelineRunnerExtensions"/> can hold for its pipeline. Implement it for a list of your own, with a
/// parameterless constructor.
/// </summary>
public interface IChangeList
{
    /// <summary>
    /// Forgets everything, pushed or done, for its next use.
    /// </summary>
    void Clear();
}
