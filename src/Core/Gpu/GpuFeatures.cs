using System.Collections.Immutable;

namespace Magic.Gpu;

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
/// The GPU's sizes: what a gem fits its tables and dispatches to.
/// </summary>
public readonly record struct GpuLimits
{
    public required string DeviceName { get; init; }

    /// <summary>
    /// Whether the device is a software one, such as lavapipe on a machine with no GPU.
    /// </summary>
    public required bool IsSoftware { get; init; }

    /// <summary>
    /// Buffers, textures and samplers the heap holds at once, each.
    /// </summary>
    public required int HeapCapacity { get; init; }

    /// <summary>
    /// The most groups a dispatch may have along one axis.
    /// </summary>
    public required uint MaxDispatchGroups { get; init; }

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
/// How long one named stretch of the frame took on the GPU, in milliseconds.
/// </summary>
public readonly record struct GpuTiming(string Name, double Milliseconds);

/// <summary>
/// The GPU's own timing of a finished frame: each pass by name, and each <see cref="GpuCommandRecorder.Timestamp"/>
/// as the time since the pass began. Two frames behind what is being recorded, as the GPU is.
/// </summary>
public readonly record struct GpuTimings(uint FrameIndex, ImmutableArray<GpuTiming> Passes)
{
    public static GpuTimings None => new(0, []);
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
