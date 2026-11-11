namespace Magic.Graphics;

/// <summary>
/// What the GPU has beyond the baseline every GPU Magic runs on. A renderer takes a path that needs one only when it
/// is there, and has a path that works without it.
/// </summary>
public readonly record struct GpuFeatures
{
    /// <summary>
    /// Ray queries against acceleration structures from any shader.
    /// </summary>
    public bool HasRayQuery { get; init; }
}
