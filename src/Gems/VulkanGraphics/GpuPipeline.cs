using System.Collections.Immutable;
using Magic.Graphics;

namespace Magic.VulkanGraphics;

/// <summary>
/// A compute or graphics pipeline the GPU provider built from SPIR-V. <see cref="None"/> is no pipeline.
/// </summary>
internal readonly record struct GpuPipeline(uint Id)
{
    public static GpuPipeline None => default;

    public bool IsNone => Id == 0;
}

/// <summary>
/// How to build a compute pipeline: one entry's SPIR-V.
/// </summary>
internal readonly record struct ComputePipelineDescription
{
    public ComputePipelineDescription() { }

    public required ReadOnlyMemory<byte> Spirv { get; init; }

    public required string EntryPoint { get; init; }

    public string Name { get; init; } = string.Empty;
}

/// <summary>
/// How to build a graphics pipeline: its vertex and pixel entries, the formats of its targets, and its fixed state.
/// </summary>
/// <remarks>
/// It takes no vertex layout, because the vertex entry reads the vertices from the heap. Viewport and scissor are
/// commands.
/// </remarks>
internal readonly record struct GraphicsPipelineDescription
{
    public GraphicsPipelineDescription() { }

    public required ReadOnlyMemory<byte> VertexSpirv { get; init; }

    public required string VertexEntryPoint { get; init; }

    public required ReadOnlyMemory<byte> PixelSpirv { get; init; }

    public required string PixelEntryPoint { get; init; }

    /// <summary>
    /// The colour targets it draws into, in order; none for a depth-only draw.
    /// </summary>
    public ImmutableArray<GpuFormat> ColorFormats { get; init; } = [];

    /// <summary>
    /// The depth target it draws into; <see cref="GpuFormat.Unknown"/> for none.
    /// </summary>
    public GpuFormat DepthFormat { get; init; }

    public DepthMode Depth { get; init; }

    public BlendMode Blend { get; init; }

    public CullMode Cull { get; init; } = CullMode.Back;

    public string Name { get; init; } = string.Empty;
}
