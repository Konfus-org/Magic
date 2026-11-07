namespace Magic.Rendering;

/// <summary>
/// What a buffer is for, which picks the memory it lives in and what commands may touch it.
/// </summary>
[Flags]
public enum BufferUsages : byte
{
    None = 0,

    /// <summary>
    /// Read and written by shaders through the heap. Device memory.
    /// </summary>
    Storage = 1 << 0,

    /// <summary>
    /// Bound as the index buffer of a draw.
    /// </summary>
    Index = 1 << 1,

    /// <summary>
    /// Holds the arguments of indirect dispatches and draws.
    /// </summary>
    Indirect = 1 << 2,

    /// <summary>
    /// Written by the CPU and copied from: host memory the GPU reads once. A buffer the frame's staging ring is made of.
    /// </summary>
    Upload = 1 << 3,

    /// <summary>
    /// Copied into and read by the CPU through <see cref="IGraphicsApi.ReadBackAsync"/>: host memory the GPU writes.
    /// </summary>
    Readback = 1 << 4,

    /// <summary>
    /// Holds an acceleration structure: what <see cref="IGraphicsApi.CreateAcceleration(in BottomLevelDescription)"/>
    /// makes for itself.
    /// </summary>
    Acceleration = 1 << 5,
}

/// <summary>
/// How to make a buffer.
/// </summary>
public readonly record struct BufferDescription
{
    public BufferDescription() { }

    public required long Bytes { get; init; }

    public required BufferUsages Usage { get; init; }

    /// <summary>
    /// What a GPU debugger shows it as.
    /// </summary>
    public string Name { get; init; } = string.Empty;
}
