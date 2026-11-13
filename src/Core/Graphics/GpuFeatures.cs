namespace Magic.Graphics;

/// <summary>
/// What the GPU has beyond the baseline that every GPU Magic runs on has.
/// </summary>
/// <remarks>
/// Use a feature only when it is there, and keep a path that works without it.
/// </remarks>
public readonly record struct GpuFeatures
{
    /// <summary>
    /// Ray queries against acceleration structures from any shader.
    /// </summary>
    public bool HasRayQuery { get; init; }
}
