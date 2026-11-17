using System.Collections.Immutable;

namespace Magic.Graphics;

/// <summary>
/// A compute shader that a library brings built, as SPIR-V, and whose resources are bound at the slots its code
/// names, not through the bindless heap.
/// </summary>
/// <remarks>
/// Its pipeline is built on the first ask and kept by its SPIR-V, so give the same memory each frame. The layout comes
/// from the kinds of its bindings, which are the same for each use of the same SPIR-V. Run it with
/// <see cref="RenderGraph.Compute(string, in BoundShader, RenderStage)"/>.
/// </remarks>
public readonly record struct BoundShader
{
    public BoundShader() { }

    public required ReadOnlyMemory<byte> Spirv { get; init; }

    public required string EntryPoint { get; init; }

    public required ImmutableArray<GpuBinding> Bindings { get; init; }
}

/// <summary>
/// What a binding slot of a <see cref="BoundShader"/> holds.
/// </summary>
public enum GpuBindingKind : byte
{
    /// <summary>
    /// A <see cref="GpuSampler"/>.
    /// </summary>
    Sampler,

    /// <summary>
    /// A range of a buffer read as constants: of this frame's staging ring (<see cref="IGraphicsApi.BeginUpload"/>),
    /// at an offset that is a multiple of 256 bytes.
    /// </summary>
    Constants,

    /// <summary>
    /// A texture that the shader reads.
    /// </summary>
    Texture,

    /// <summary>
    /// A texture that the shader writes, at level 0.
    /// </summary>
    StorageTexture,
}

/// <summary>
/// One resource at the descriptor set <paramref name="Set"/> and binding <paramref name="Slot"/> that a
/// <see cref="BoundShader"/>'s code names. <paramref name="Offset"/> and <paramref name="Size"/> are for
/// <see cref="GpuBindingKind.Constants"/> alone.
/// </summary>
public readonly record struct GpuBinding(
    uint Set,
    uint Slot,
    GpuBindingKind Kind,
    uint Id,
    ulong Offset = 0,
    ulong Size = 0
);
