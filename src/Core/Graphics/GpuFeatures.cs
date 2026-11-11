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

/// <summary>
/// Room in this frame's staging ring to write into, and where the GPU reads it from: the buffer and offset a copy
/// command names. Good for the frame it was begun in and gone after.
/// </summary>
public readonly ref struct GpuUpload(GpuBuffer buffer, ulong offset, Span<byte> bytes)
{
    public GpuBuffer Buffer { get; } = buffer;

    public ulong Offset { get; } = offset;

    public Span<byte> Bytes { get; } = bytes;
}
