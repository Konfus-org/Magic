using Magic.Attributes.Assets;
using Magic.Contexts.Rendering;

namespace Magic.Contexts.Assets;

/// <summary>
/// What a core pass does with its shader.
/// </summary>
public enum PassKind : byte
{
    /// <summary>
    /// A <c>.comp.hlsl</c> dispatched as <see cref="Pass.Dispatch"/> says.
    /// </summary>
    Compute,

    /// <summary>
    /// A <c>.frag.hlsl</c> drawn as one triangle over the texture it writes.
    /// </summary>
    Fullscreen,

    /// <summary>
    /// The scene's meshes, as the culling left them in an indirect draw buffer (<see cref="Pass.Draw"/>).
    /// </summary>
    Draw,

    /// <summary>
    /// Quads from the vertex id alone: six vertices per row of <see cref="PassDraw.Rows"/>, the pass's vertex shader
    /// reading what it needs of the buffer it reads first (the glows, the tiles of a shadow atlas to clear).
    /// </summary>
    Quads
}

/// <summary>
/// How a sampled texture is read; <see cref="Default"/> is nearest for depth and single-float textures, linear otherwise.
/// </summary>
public enum ReadFilter : byte
{
    Default,
    Nearest,
    Linear,

    /// <summary>
    /// A comparison sampler, for a shadow map read with <c>SampleCmpLevelZero</c>.
    /// </summary>
    Comparison
}

/// <summary>
/// Pixel format of a texture or volume a pass creates; <see cref="Depth"/> is the device's depth format.
/// </summary>
public enum PassFormat : byte
{
    Rgba8Srgb,
    Rgba8Unorm,
    Rgba16Float,
    R16Float,
    R32Float,
    Rg16Float,
    B10G11R11Float,
    R8Unorm,
    R32Uint,
    Rgba32Float,
    Depth
}

public enum ResourceKind : byte
{
    Texture,

    /// <summary>
    /// A 3D texture, <see cref="PassCreate.Levels"/> slabs of <see cref="PassCreate.Size"/> stacked along its depth.
    /// </summary>
    Volume,
    Buffer
}

/// <summary>
/// What a created buffer is for, beyond being read and written by compute: <see cref="Indirect"/> holds draw or
/// dispatch arguments, <see cref="Vertex"/> is bound as an instance-rate vertex buffer by the draws.
/// </summary>
public enum BufferUsage : byte
{
    Storage,
    Indirect,
    Vertex
}

/// <summary>
/// What a pass that runs several times iterates over.
/// </summary>
public enum EachOver : byte
{
    /// <summary>
    /// <see cref="PassEach.Max"/> times, the iteration in <c>PassIteration()</c>.
    /// </summary>
    Count,

    /// <summary>
    /// Once per occupancy brick job the meshes queued this frame, at most <see cref="PassEach.Max"/>.
    /// </summary>
    Bricks
}

/// <summary>
/// Which pipelines a draw pass draws with.
/// </summary>
public enum DrawPipeline : byte
{
    /// <summary>
    /// The opaque material pipelines, composed by the engine around each surface shader.
    /// </summary>
    Material,

    /// <summary>
    /// The transparent material pipelines, lit as they are drawn.
    /// </summary>
    Forward,

    /// <summary>
    /// The pass's own depth-only pipeline (<see cref="Pass.Shader"/> and <see cref="Pass.Fragment"/>), for shadow maps.
    /// </summary>
    Depth
}

/// <summary>
/// One resource a pass reads, bound in the order written: textures first (each with a sampler), then buffers.
/// </summary>
public sealed class PassRead
{
    public string Name { get; set; } = "";

    public ReadFilter Filter { get; set; }
}

/// <summary>
/// One resource a pass writes: a texture (one mip <see cref="Level"/> and <see cref="Layer"/> or 3D slice of it) or a buffer.
/// For a fullscreen pass, its one colour target and what the pass does with what was there.
/// </summary>
public sealed class PassWrite
{
    public string Name { get; set; } = "";

    public uint Level { get; set; }

    public uint Layer { get; set; }

    public GpuLoad Load { get; set; } = GpuLoad.DontCare;
}

/// <summary>
/// A resource a pass makes, by name, for itself and every pass after it. A texture with a <see cref="Scale"/> follows the
/// render target's size; one with a <see cref="Size"/>, a volume or a buffer is fixed. A buffer is sized by
/// <see cref="Bytes"/> or by a frame count (<see cref="Per"/>, a <c>$name</c>) times <see cref="Stride"/>, never under
/// <see cref="Min"/>, and <see cref="Slots"/> copies of that when a pass runs several times over its own slice.
/// </summary>
public sealed class PassCreate
{
    public string Name { get; set; } = "";

    public ResourceKind Kind { get; set; }

    public PassFormat Format { get; set; }

    /// <summary>
    /// Of the render target's size; 0 means <see cref="Size"/> is fixed.
    /// </summary>
    public float Scale { get; set; } = 1f;

    /// <summary>
    /// A fixed texture's width and height, or one slab of a volume: width, height, depth.
    /// </summary>
    public uint[] Size { get; set; } = [];

    /// <summary>
    /// Slabs a volume stacks along its depth.
    /// </summary>
    public uint Levels { get; set; } = 1;

    public uint Bytes { get; set; }

    /// <summary>
    /// The frame count a buffer is sized by: <c>$pageCount</c>, <c>$groupCount</c>, <c>$visibleHighWater</c>, ...
    /// </summary>
    public string Per { get; set; } = "";

    public uint Stride { get; set; } = 4;

    /// <summary>
    /// Bytes on top of the rows: a count word before them, say.
    /// </summary>
    public uint Plus { get; set; }

    public uint Min { get; set; } = 16;

    public uint Slots { get; set; } = 1;

    public BufferUsage Usage { get; set; }

    /// <summary>
    /// An engine buffer whose contents are copied into this one every frame, before anything runs.
    /// </summary>
    public string Seed { get; set; } = "";

    /// <summary>
    /// Words uploaded at the start of the buffer every frame: numbers, or a <c>$count</c> name.
    /// </summary>
    public string[] Reset { get; set; } = [];

    /// <summary>
    /// Zero-filled whenever it is made.
    /// </summary>
    public bool Zero { get; set; }

    /// <summary>
    /// A buffer kept twice, used in turn: <see cref="Name"/> is this frame's, <c>&lt;Name&gt;Previous</c> last frame's.
    /// </summary>
    public bool History { get; set; }
}

/// <summary>
/// How many groups a compute pass dispatches: enough threads for every texel of <c>output:Name</c> (a texture or volume
/// it writes) or <c>view</c> (the view's rectangle; with a <see cref="Tile"/>, one group per tile of that many pixels
/// a side instead, and <see cref="Depth"/> groups deep), one thread per <c>$count</c>, fixed <see cref="Groups"/>, or
/// the groups read from an <see cref="Indirect"/> buffer at <c>iteration * IndirectStride</c>.
/// </summary>
public sealed class PassDispatch
{
    public string Per { get; set; } = "";

    public uint Tile { get; set; }

    public uint Depth { get; set; }

    public uint[] Groups { get; set; } = [];

    public string Indirect { get; set; } = "";

    public uint IndirectStride { get; set; }
}

public sealed class PassEach
{
    public EachOver Over { get; set; }

    public uint Max { get; set; } = 1;
}

/// <summary>
/// What a draw or quads pass draws into and from: its colour targets in <c>SV_Target</c> order (none for a depth-only
/// draw), its depth, and for a draw the indirect <see cref="Args"/> buffer (one chunk per draw call; in the shadows
/// stage, one slice per iteration) and the <see cref="Instances"/> vertex buffer the culling filled. The stage says
/// whose views it draws: the camera's in the scene and transparency stages, the shadow views in the shadows stage.
/// </summary>
public sealed class PassDraw
{
    public DrawPipeline Pipeline { get; set; }

    public string[] Colors { get; set; } = [];

    public string Depth { get; set; } = "";

    public GpuLoad Load { get; set; } = GpuLoad.Load;

    public string Args { get; set; } = "";

    public string Instances { get; set; } = "";

    public float DepthBiasSlope { get; set; }

    /// <summary>
    /// For quads: how many rows, a <c>$count</c> or a number.
    /// </summary>
    public string Rows { get; set; } = "";

    public GpuCompare DepthCompare { get; set; } = GpuCompare.GreaterOrEqual;

    public bool DepthWrite { get; set; } = true;
}

/// <summary>
/// One step of the render pipeline, as data: a <c>.pass</c> file. The pipeline asset lists it in a stage, and the stage
/// says what it runs over: once a frame (shadows, GI), once per view (the scene, the lighting, the transparency) or
/// once per render target (the sky). Its shader gets the frame block and its <see cref="Params"/>, packed into the
/// shader's <c>struct PassParams</c> the way a material's fill <c>MaterialParams</c>; the resources it <see cref="Reads"/>
/// bind in the order written, sampled textures first, then storage textures, then buffers; what it <see cref="Writes"/>
/// is a texture or buffer the engine provides or one a pass <see cref="Creates"/>. A pass that does not fit (a name
/// nothing made, a shader that reads other bindings than it declares) is disabled with one logged line and shown as a
/// failure on screen.
/// </summary>
[AssetFormat(AssetFormat.Json)]
public sealed class Pass : Asset
{
    public PassKind Kind { get; set; }

    /// <summary>
    /// The compute or fragment shader; for a depth draw or the glows, the vertex shader.
    /// </summary>
    public Handle<Shader> Shader { get; set; }

    /// <summary>
    /// The fragment shader of a depth draw or the glows; a material draw has none, the engine composes it.
    /// </summary>
    public Handle<Shader> Fragment { get; set; }

    /// <summary>
    /// Whole <c>#define NAME value</c> lines put in front of the shader.
    /// </summary>
    public string[] Defines { get; set; } = [];

    public PassEach Each { get; set; } = new();

    /// <summary>
    /// A <c>$count</c> the pass only runs for: when it is 0 the pass and everything it creates are left out this frame,
    /// and nothing that depends on it runs either. <c>$shadowCasters</c>, say, for the shadow maps.
    /// </summary>
    public string Needs { get; set; } = "";

    public PassRead[] Reads { get; set; } = [];

    public PassWrite[] Writes { get; set; } = [];

    public PassCreate[] Creates { get; set; } = [];

    public PassDispatch Dispatch { get; set; } = new();

    public PassDraw Draw { get; set; } = new();

    public Dictionary<string, Param> Params { get; set; } = [];
}
