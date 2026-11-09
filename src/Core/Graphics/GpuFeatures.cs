namespace Magic.Graphics;

/// <summary>
/// What the GPU has beyond the baseline every GPU Magic runs on. A renderer takes a path that needs one only when it
/// is there, and has a path that works without it.
/// </summary>
public readonly record struct GpuFeatures
{
    /// <summary>
    /// Task and mesh shaders in place of vertex shaders for cluster drawing.
    /// </summary>
    public bool HasMeshShaders { get; init; }

    /// <summary>
    /// Ray queries against acceleration structures from any shader.
    /// </summary>
    public bool HasRayQuery { get; init; }

    /// <summary>
    /// 64-bit atomics on buffers, which a software rasteriser needs.
    /// </summary>
    public bool HasAtomic64 { get; init; }

    /// <summary>
    /// 64-bit atomics on storage images.
    /// </summary>
    public bool HasImageAtomic64 { get; init; }

    /// <summary>
    /// Samplers that take the least or greatest texel rather than their blend.
    /// </summary>
    public bool HasSamplerMinMax { get; init; }
}

/// <summary>
/// What the GPU is and holds: its name, the alignment an upload's offset takes and its device memory.
/// </summary>
public readonly record struct GpuLimits
{
    public required string DeviceName { get; init; }

    /// <summary>
    /// Bytes an upload's offset is a multiple of.
    /// </summary>
    public required int UploadAlignment { get; init; }

    /// <summary>
    /// Bytes of device memory the GPU has for the heap's resources.
    /// </summary>
    public required long DeviceMemoryBytes { get; init; }
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
