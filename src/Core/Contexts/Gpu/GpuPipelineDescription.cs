using Magic.Interfaces;

namespace Magic.Contexts.Gpu;

/// <summary>
/// The type of a vertex input.
/// </summary>
public enum GpuVertexFormat : byte
{
    Float1,
    Float2,
    Float3,
    Float4,
    Uint1,

    /// <summary>
    /// Four bytes, read as 0 to 1.
    /// </summary>
    Ubyte4Norm,
}

/// <summary>
/// Which faces a pipeline does not draw. Mirrored geometry (a negative scale) draws with <see cref="Front"/>.
/// </summary>
public enum GpuCull : byte
{
    None,
    Back,
    Front,
}

/// <summary>
/// How a colour target blends what a fragment writes (src) into what is there (dst).
/// </summary>
public enum GpuBlend : byte
{
    /// <summary>
    /// src replaces dst.
    /// </summary>
    None,

    /// <summary>
    /// Straight alpha over what is there: src * src.a + dst * (1 - src.a).
    /// </summary>
    Alpha,

    /// <summary>
    /// Premultiplied alpha over what is there: src + dst * (1 - src.a).
    /// </summary>
    Premultiplied,

    /// <summary>
    /// src + dst, every channel.
    /// </summary>
    Add,

    /// <summary>
    /// The larger of src and dst, every channel.
    /// </summary>
    Max,
}

/// <summary>
/// One vertex buffer slot of a pipeline: its stride in bytes, and whether it steps per instance rather than per vertex.
/// </summary>
public readonly record struct GpuVertexBuffer(uint Slot, uint Stride, bool PerInstance = false);

/// <summary>
/// One vertex shader input: the location it is read at (<c>TEXCOORDn</c>), the <see cref="GpuVertexBuffer.Slot"/> of
/// the buffer it comes from, its format, and its byte offset within a vertex.
/// </summary>
public readonly record struct GpuVertexInput(uint Location, uint Slot, GpuVertexFormat Format, uint Offset);

/// <summary>
/// A graphics pipeline for <see cref="IGpu.CreatePipeline(GpuPipelineDescription)"/> to make: both shaders, the vertex
/// layout, the colour targets (one per <c>SV_Target</c> the fragment shader writes, in order; none for a depth-only
/// pipeline) and an optional depth target. Triangles are clockwise from outside, and the defaults are the engine's
/// other conventions (back faces culled, reverse-Z), so a GPU reads them rather than knowing them. Depth is tested
/// whenever <see cref="DepthFormat"/> is set, and written unless <see cref="DepthWrite"/> is off.
/// </summary>
public sealed record GpuPipelineDescription
{
    public required GpuShader Vertex { get; init; }

    public required GpuShader Fragment { get; init; }

    public IReadOnlyList<GpuFormat> ColorFormats { get; init; } = [];

    /// <summary>
    /// How each colour target blends what the fragment writes into what is there, in the order of
    /// <see cref="ColorFormats"/>; a target past the end does not blend.
    /// </summary>
    public IReadOnlyList<GpuBlend> Blends { get; init; } = [];

    public IReadOnlyList<GpuVertexBuffer> VertexBuffers { get; init; } = [];

    public IReadOnlyList<GpuVertexInput> VertexInputs { get; init; } = [];

    /// <summary>
    /// The depth target's format, or <see cref="GpuFormat.Invalid"/> for none.
    /// </summary>
    public GpuFormat DepthFormat { get; init; }

    public GpuCompare DepthCompare { get; init; } = GpuCompare.GreaterOrEqual;

    public bool DepthWrite { get; init; } = true;

    public GpuCull Cull { get; init; } = GpuCull.Back;

    /// <summary>
    /// The rasteriser's depth bias: a constant, in the depth format's smallest steps, and a factor of the slope, both 0
    /// for none. Reverse-Z: negative pushes depth away from the viewer.
    /// </summary>
    public float DepthBias { get; init; }

    public float DepthBiasSlope { get; init; }

    public float DepthBiasClamp { get; init; }

    /// <summary>
    /// Off clamps depth to the planes instead of clipping at them, so a shadow caster behind the near plane still
    /// writes.
    /// </summary>
    public bool DepthClip { get; init; } = true;
}
