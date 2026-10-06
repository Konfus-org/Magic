namespace Magic.Contexts.Rendering;

/// <summary>
/// A buffer made by <see cref="Interfaces.IRendering.CreateBuffer"/>; 0 is none.
/// </summary>
public readonly record struct GpuBuffer(uint Id)
{
    public bool IsValid => Id != 0;
}

/// <summary>
/// A texture made by <see cref="Interfaces.IRendering.CreateTexture"/>, or a window's swapchain image
/// (<see cref="Window"/>), which the renderer acquires the first time a command of the frame uses it; 0 is none.
/// </summary>
public readonly record struct GpuTexture(uint Id)
{
    private const uint WindowBit = 0x8000_0000;

    public bool IsValid => Id != 0;

    public bool IsWindow => (Id & WindowBit) != 0;

    /// <summary>
    /// The <see cref="Interfaces.IWindow.Handle"/> of a swapchain image.
    /// </summary>
    public uint WindowHandle => Id & ~WindowBit;

    /// <summary>
    /// This frame's swapchain image of the window with this <see cref="Interfaces.IWindow.Handle"/>.
    /// </summary>
    public static GpuTexture Window(uint window)
    {
        return new GpuTexture(WindowBit | window);
    }
}

public readonly record struct GpuSampler(uint Id)
{
    public bool IsValid => Id != 0;
}

/// <summary>
/// A graphics or compute pipeline; which one it is was fixed when it was made.
/// </summary>
public readonly record struct GpuPipeline(uint Id)
{
    public bool IsValid => Id != 0;
}

public enum GpuStage : byte
{
    Vertex,
    Fragment,
    Compute
}

/// <summary>
/// The texture formats the engine uses; <see cref="Interfaces.IRendering.DepthFormat"/> says which depth one the device has.
/// </summary>
public enum GpuFormat : byte
{
    Invalid,
    Rgba8Unorm,
    Rgba8Srgb,
    Bgra8Unorm,
    Bgra8Srgb,
    Rgba16Float,
    R16Float,
    R32Float,
    Rg16Float,
    R11G11B10Float,
    Rgb10A2Unorm,
    D32Float,
    D24Unorm,
    R32Uint,
    R8Unorm,
    R16Unorm,
    Rgba32Float
}

[Flags]
public enum GpuBufferUsage : byte
{
    None = 0,
    Vertex = 1 << 0,
    Index = 1 << 1,
    Indirect = 1 << 2,
    GraphicsRead = 1 << 3,
    ComputeRead = 1 << 4,
    ComputeWrite = 1 << 5
}

[Flags]
public enum GpuTextureUsage : byte
{
    None = 0,
    Sampler = 1 << 0,
    ColorTarget = 1 << 1,
    DepthTarget = 1 << 2,
    ComputeWrite = 1 << 3,

    /// <summary>
    /// Read in a compute shader as a storage texture (no sampler), through <see cref="RenderCommands.BindStorageTextures"/>.
    /// </summary>
    ComputeRead = 1 << 4
}

public enum GpuTextureKind : byte
{
    Texture2D,
    Texture2DArray,
    Texture3D
}

public enum GpuFilter : byte
{
    Linear,
    Nearest
}

public enum GpuAddress : byte
{
    Clamp,
    Repeat
}

public enum GpuCull : byte
{
    None,
    Back,
    Front
}

public enum GpuFrontFace : byte
{
    Clockwise,
    CounterClockwise
}

public enum GpuCompare : byte
{
    Always,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
    Never
}

public enum GpuVertexFormat : byte
{
    Float,
    Float2,
    Float3,
    Float4,
    Uint,
    Ubyte4Norm
}

/// <summary>
/// What a render pass does with a target's old contents.
/// </summary>
public enum GpuLoad : byte
{
    Load,
    Clear,
    DontCare
}

/// <summary>
/// A texture to make. <paramref name="Layers"/> above 1 makes a <see cref="GpuTextureKind.Texture2DArray"/> unless <paramref name="Kind"/>
/// says otherwise; for a <see cref="GpuTextureKind.Texture3D"/> it is the depth.
/// </summary>
public readonly record struct TextureDesc(GpuFormat Format, GpuTextureUsage Usage, uint Width, uint Height, uint Levels = 1, uint Layers = 1, GpuTextureKind Kind = GpuTextureKind.Texture2D);

/// <summary>
/// A sampler; with <paramref name="Compare"/> a comparison sampler (<c>SamplerComparisonState</c>): a sample is 1 where
/// <c>reference op stored</c> holds, filtered over the taps.
/// </summary>
public readonly record struct SamplerDesc(GpuFilter Filter, GpuAddress Address, float Anisotropy = 0f, GpuCompare? Compare = null);

/// <summary>
/// One mip level of one layer, or a rectangle of it; <see cref="Width"/> and <see cref="Height"/> of 0 mean the whole level.
/// <see cref="Z"/> and <see cref="Depth"/> are the slices of a 3D texture, 0 depth being all of them.
/// </summary>
public readonly record struct TextureRegion(GpuTexture Texture, uint Level = 0, uint Layer = 0, uint X = 0, uint Y = 0, uint Width = 0, uint Height = 0, uint Z = 0, uint Depth = 0);

/// <summary>
/// One vertex buffer slot of a pipeline: its stride and whether it steps per instance.
/// </summary>
public readonly record struct VertexBufferLayout(uint Slot, uint Pitch, bool PerInstance = false);

public readonly record struct VertexAttribute(uint Location, uint Slot, GpuVertexFormat Format, uint Offset);

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
    /// Straight alpha over what is there: src * src.a + dst * (1 - src.a), for UI.
    /// </summary>
    Alpha,

    /// <summary>
    /// src + dst, every channel: sums such as weighted transparency.
    /// </summary>
    Add,

    /// <summary>
    /// src + dst * (1 - src), every channel: coverage that builds up as 1 - the product of what lets light through.
    /// </summary>
    Coverage,

    /// <summary>
    /// The larger of src and dst, every channel: the nearest depth (reverse-Z) of what was drawn.
    /// </summary>
    Max
}

/// <summary>
/// A graphics pipeline, as data: both shaders, the vertex layout, the colour targets (one per <c>SV_Target</c> the
/// fragment shader writes, in order; none for a depth-only pipeline) and an optional depth target. The defaults are the
/// engine's conventions (clockwise front faces, reverse-Z), so a backend reads them rather than knowing them.
/// Depth is tested whenever <see cref="Depth"/> is set, and written unless <see cref="DepthWrite"/> is off.
/// </summary>
public sealed record PipelineDesc(CompiledShader Vertex, CompiledShader Fragment, params GpuFormat[] Colors)
{
    public VertexBufferLayout[] Buffers { get; init; } = [];

    public VertexAttribute[] Attributes { get; init; } = [];

    public GpuFormat Depth { get; init; }

    public GpuCull Cull { get; init; } = GpuCull.Back;

    public GpuFrontFace FrontFace { get; init; } = GpuFrontFace.Clockwise;

    public GpuCompare DepthCompare { get; init; } = GpuCompare.GreaterOrEqual;

    /// <summary>
    /// How each colour target blends what the fragment writes into what is there, in the order of <see cref="Colors"/>;
    /// a target past the end does not blend.
    /// </summary>
    public GpuBlend[] Blends { get; init; } = [];

    public bool DepthWrite { get; init; } = true;

    /// <summary>
    /// The rasteriser's depth bias: a constant (in the depth format's smallest steps) and a slope factor, both 0 for none.
    /// Reverse-Z: negative pushes depth away from the viewer.
    /// </summary>
    public float DepthBias { get; init; }

    public float DepthBiasSlope { get; init; }

    public float DepthBiasClamp { get; init; }

    /// <summary>
    /// Off clamps depth to the planes instead of clipping at them, so a shadow caster behind the near plane still writes.
    /// </summary>
    public bool DepthClip { get; init; } = true;
}

/// <summary>
/// HLSL as the backend compiled it: bytecode in the device's format plus the binding counts it needs to make a shader.
/// What <see cref="Interfaces.IRendering.Compile"/> returns and Core caches on disk.
/// </summary>
public sealed class CompiledShader
{
    public byte[] Code { get; set; } = [];

    public GpuStage Stage { get; set; }

    public uint Samplers { get; set; }

    public uint StorageTextures { get; set; }

    public uint StorageBuffers { get; set; }

    public uint UniformBuffers { get; set; }

    public uint ReadWriteStorageTextures { get; set; }

    public uint ReadWriteStorageBuffers { get; set; }

    public uint ThreadCountX { get; set; }

    public uint ThreadCountY { get; set; }

    public uint ThreadCountZ { get; set; }
}

/// <summary>
/// One binding of a run a command binds: a buffer, or a texture with (for sampling) its sampler. <see cref="Level"/> and
/// <see cref="Layer"/> pick the mip and layer (or 3D slice) a compute pass writes a storage texture at.
/// </summary>
public readonly record struct GpuBinding(GpuBuffer Buffer = default, GpuTexture Texture = default, GpuSampler Sampler = default, uint Level = 0, uint Layer = 0);

/// <summary>
/// One captured frame: 8-bit RGBA, rows tightly packed top to bottom, <c>Width * Height * 4</c> bytes.
/// </summary>
public readonly record struct CapturedFrame(int Width, int Height, byte[] Pixels);
