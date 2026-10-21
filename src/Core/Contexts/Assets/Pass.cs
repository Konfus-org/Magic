using Magic.Contexts.Gpu;
using Magic.Interfaces;

namespace Magic.Contexts.Assets;

/// <summary>
/// When in a frame a <see cref="Pass"/> runs, in this order, each stage one job: a pass goes in the stage whose job it
/// does, and the stages order the passes of every pipeline the frame runs (the renderer's own, each effect a world
/// adds) together, so a pass lands where what it reads is made and what reads it comes after, whatever a pipeline lists
/// it with. The stages before <see cref="Cull"/> run once a frame, for every view to read; <see cref="Cull"/> to
/// <see cref="Ui"/> once for each view, a camera drawing into its target; <see cref="Overlay"/> once for each target,
/// after every view drawn into it.
/// </summary>
public enum PassStage : byte
{
    /// <summary>
    /// Light gathered once a frame for every view to read: what sees the world rather than a view, the sky captured and
    /// its spherical harmonics, probes of the light about the camera, and the tables lighting looks up. Here because it
    /// is the same for every view, and every stage of a view may read it.
    /// </summary>
    Probes,

    /// <summary>
    /// The scene culled into the view's draws: what the view can see, and how finely to draw it. First of a view's
    /// stages, as every draw after it draws what it kept.
    /// </summary>
    Cull,

    /// <summary>
    /// Which surface each pixel sees, drawn before anything is shaded: a depth pre-pass or a visibility buffer, and
    /// what is built from it, a depth pyramid and the culling that tests against it. Here so every stage after it knows
    /// what is in front, and nothing is shaded that is then covered.
    /// </summary>
    Visibility,

    /// <summary>
    /// What each pixel's surface is made of, shaded once a pixel into the view's G-buffer, every target at once; not
    /// yet lit, so lighting and everything that informs it read one description of the pixel.
    /// </summary>
    GBuffer,

    /// <summary>
    /// Which lights reach which pixels: the view's lights binned by depth and by tile, so lighting, and whatever before
    /// it asks which lights touch a pixel, walks only those. After the G-buffer, as it reads the view's depth.
    /// </summary>
    LightCulling,

    /// <summary>
    /// What blocks each light, drawn for lighting to read: the shadow maps, or the pages of them the view's pixels
    /// read. After light culling, as which shadows a pixel needs follows from which lights reach it.
    /// </summary>
    Shadows,

    /// <summary>
    /// What blocks light that no shadow map can hold, worked out on the screen from the G-buffer: ambient occlusion,
    /// contact shadows. A separate stage so a world adds it, or not, as an effect of its own.
    /// </summary>
    Occlusion,

    /// <summary>
    /// What each pixel reflects, traced through the view and whatever else the renderer holds of the world, filtered,
    /// for lighting to take in place of the sky's reflection. Its own stage so a world adds reflections without
    /// occlusion, and the other way about.
    /// </summary>
    Reflections,

    /// <summary>
    /// The G-buffer lit into the view's scene colour: every light, shadow, occlusion and reflection taken in once a
    /// pixel.
    /// </summary>
    Lighting,

    /// <summary>
    /// What fills the view wherever nothing was drawn, and what lies between the camera and what was: the sky, fog.
    /// Over the lit scene, before anything transparent, which is drawn over it.
    /// </summary>
    Sky,

    /// <summary>
    /// Transparent surfaces over the lit scene, farthest first, and what they read of the scene behind them.
    /// </summary>
    Transparency,

    /// <summary>
    /// The finished scene made ready to show: exposure, upscaling, tonemapping, any effect over the whole image. After
    /// it the view's image is in the colours it is shown in.
    /// </summary>
    Post,

    /// <summary>
    /// What is drawn over a view's finished image, for that view: a player's HUD, the text and marks of their game. Its
    /// own stage so it comes after every post effect whatever order a world's effects list them, and before the view
    /// is shown in its target.
    /// </summary>
    Ui,

    /// <summary>
    /// What is drawn over a target once every view in it is composed, in the colours the target shows, as
    /// <c>Target</c>: the debug UI, a cursor, a banner, anything that belongs to the window rather than to a view. A
    /// target's own scope, so it is drawn once however many views share the target.
    /// </summary>
    Overlay,
}

/// <summary>
/// What a <see cref="Pass"/> does with its shader.
/// </summary>
public enum PassKind : byte
{
    /// <summary>
    /// A compute shader, run as <see cref="Pass.Dispatch"/> says.
    /// </summary>
    Compute,

    /// <summary>
    /// A fragment shader drawn as one triangle over the one texture it writes.
    /// </summary>
    Fullscreen,

    /// <summary>
    /// Meshes or the sky, as <see cref="Pass.Draw"/> says.
    /// </summary>
    Draw,

    /// <summary>
    /// Code: the pass's <see cref="Pass.Script"/>, an <see cref="IPipelineScript"/>, records it into the one texture it
    /// writes, with pipelines of its own.
    /// </summary>
    Script,
}

/// <summary>
/// How a texture is sampled wherever a shader samples it (<see cref="PassCreate.Filter"/>).
/// </summary>
public enum PassFilter : byte
{
    /// <summary>
    /// Nearest for depth and single-channel textures, linear for the rest.
    /// </summary>
    Default,

    Nearest,
    Linear,

    /// <summary>
    /// A comparison sampler, for a shadow map read with <c>SampleCmpLevelZero</c>.
    /// </summary>
    Comparison,
}

/// <summary>
/// What a <see cref="PassCreate"/> makes.
/// </summary>
public enum PassResource : byte
{
    Texture,
    Buffer,
}

/// <summary>
/// Which of its view's sizes a <see cref="PassCreate"/>'s texture follows.
/// </summary>
public enum PassSize : byte
{
    /// <summary>
    /// The size the view is drawn at: its target's scaled by the renderer's render scale, or the size the renderer is
    /// told to draw at.
    /// </summary>
    View,

    /// <summary>
    /// The size the view is shown at, its target's: what upscales the view, and what follows it. The drawn size instead
    /// while the renderer's upscale is off, which leaves the scaling to the output pass.
    /// </summary>
    Output,
}

/// <summary>
/// What a draw <see cref="Pass"/> draws.
/// </summary>
public enum PassDrawn : byte
{
    /// <summary>
    /// Opaque and masked surfaces, each with its material's surface shader, into the G-buffer.
    /// </summary>
    Surfaces,

    /// <summary>
    /// Transparent surfaces, farthest first, each lit as it is drawn over what is behind it.
    /// </summary>
    Transparent,

    /// <summary>
    /// Opaque surfaces' depth alone, with the pass's own shaders: shadow maps.
    /// </summary>
    Depth,

    /// <summary>
    /// The world's sky, wherever nothing was drawn.
    /// </summary>
    Sky,

    /// <summary>
    /// The world's sky's shadow on the ground around the camera: the share of the sun's light that gets through it,
    /// into a single-channel target, which whatever the sun lights reads.
    /// </summary>
    SkyShadow,

    /// <summary>
    /// The air between a view's camera and what it sees, from the world's sky: what it scatters toward the camera and
    /// lets through, a froxel at a time, a slice of depth an iteration, which the air and fog over the view reads.
    /// </summary>
    SkyAir,

    /// <summary>
    /// Which triangle each pixel sees, and its depth, a culling pass's pieces of the scene drawn into the view's
    /// visibility buffer: what a renderer that shades each pixel once draws first.
    /// </summary>
    Visibility,
}

/// <summary>
/// One step of a renderer's frame, as data: a <c>.pass</c> file. It says when in the frame it runs
/// (<see cref="Stage"/>), what it is (<see cref="Kind"/>), the shader it runs or the script that records it, the
/// textures and buffers it writes by name, those it makes for itself and the passes after it, and values for its
/// shader's <c>PassParams</c>. A
/// <see cref="Pipeline"/> lists passes; the renderer runs each in its stage, in the order listed. What a pass reads its
/// shader says: the renderer declares to it, ahead of it, every name the frame has by then, the renderer's own and what
/// the passes before it made, and binds whichever the shader uses:
/// <c>{ "stage": "post", "kind": "fullscreen", "shader": { "id": 10230 },
/// "creates": [ { "name": "Ldr", "format": "rgba16_float" } ], "writes": [ { "name": "Ldr" } ] }</c>.
/// </summary>
public sealed class Pass : IJsonAsset<Pass>
{
    public static IReadOnlyList<string> Extensions { get; } = [".pass"];

    public Handle<Pass> Handle { get; init; }

    /// <summary>
    /// What it shows, in a sentence, for a debug view to say on screen beside its name; empty for a pass that is no
    /// view.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    public required PassStage Stage { get; init; }

    public PassKind Kind { get; set; }

    /// <summary>
    /// A compute pass's <c>.comp.hlsl</c>, a fullscreen pass's <c>.frag.hlsl</c>, or a depth draw's <c>.vert.hlsl</c>,
    /// whose fragment stage is the renderer's empty one that only writes depth; a draw of surfaces has none, the
    /// renderer composing its shaders around them.
    /// </summary>
    public Handle<Shader> Shader { get; set; }

    /// <summary>
    /// A script pass's <see cref="IPipelineScript"/>, which records it, into the one texture it writes.
    /// </summary>
    public Handle<Script> Script { get; set; }

    /// <summary>
    /// A count the renderer knows, such as <c>hizLevels</c>, or a number: the pass runs once for each, its shader told
    /// which (<c>PassIteration()</c>), writing the layer of a texture array or cube its iteration is. Empty runs it
    /// once.
    /// </summary>
    public string Each { get; set; } = string.Empty;

    /// <summary>
    /// What it writes: a compute pass's storage textures and buffers, a fullscreen pass's one target.
    /// </summary>
    public IReadOnlyList<PassWrite> Writes { get; set; } = [];

    /// <summary>
    /// What it makes, for itself and every pass after it: in a stage that runs once a frame once for the frame, in any
    /// other once for each view.
    /// </summary>
    public IReadOnlyList<PassCreate> Creates { get; set; } = [];

    /// <summary>
    /// How many groups a compute pass runs.
    /// </summary>
    public PassDispatch Dispatch { get; set; } = new();

    /// <summary>
    /// What a draw pass draws, and into what.
    /// </summary>
    public PassDraw Draw { get; set; } = new();

    /// <summary>
    /// Values by the names of the shader's <c>PassParams</c> members, in the forms a material's take; one left out
    /// takes the shader's default. What a pass runs with is set while it runs on the pipeline that runs it
    /// (<see cref="Pipeline.Params"/>), never here.
    /// </summary>
    public IReadOnlyDictionary<string, MaterialParam> Params { get; set; }
        = new Dictionary<string, MaterialParam>(StringComparer.Ordinal);
}

/// <summary>
/// One texture or buffer a <see cref="Pass"/> writes: a texture at a mip <see cref="Level"/> and a <see cref="Layer"/>,
/// or a buffer. A fullscreen pass's one target says what happens to what was there (<see cref="Contents"/>,
/// <see cref="Blend"/>).
/// </summary>
public sealed class PassWrite
{
    public required string Name { get; init; }

    public uint Level { get; set; }

    public uint Layer { get; set; }

    public GpuContents Contents { get; set; } = GpuContents.Discard;

    /// <summary>
    /// How a fullscreen pass's output blends into what its target holds: <c>add</c> to light over what is there.
    /// </summary>
    public GpuBlend Blend { get; set; }

    /// <summary>
    /// The texture's levels past the first are made from the one written once the pass has run: a blurrier copy at
    /// each, as rough glass reads what is behind it.
    /// </summary>
    public bool Mipmaps { get; set; }
}

/// <summary>
/// A texture or buffer a <see cref="Pass"/> makes, by name, for itself and every pass after it. A texture follows its
/// view's size by <see cref="Scale"/>, or is <see cref="Width"/> by <see cref="Height"/> when it is 0, and
/// <see cref="Depth"/> deep when it is a volume; a buffer holds <see cref="Rows"/>, or as many as a count the renderer
/// knows (<see cref="Per"/>), of <see cref="Stride"/> bytes each, as its shader reads them as <see cref="Type"/>. What
/// it is used for (sampled, drawn into, written by compute, read as draw arguments or instances) follows from the
/// passes whose shaders name it.
/// </summary>
public sealed class PassCreate
{
    public required string Name { get; init; }

    public PassResource Kind { get; set; }

    /// <summary>
    /// A texture's format.
    /// </summary>
    public GpuFormat Format { get; set; } = GpuFormat.Rgba16Float;

    /// <summary>
    /// How a texture is sampled wherever a shader samples it.
    /// </summary>
    public PassFilter Filter { get; set; }

    /// <summary>
    /// Of the size <see cref="Size"/> says; 0 makes the texture <see cref="Width"/> by <see cref="Height"/>.
    /// </summary>
    public float Scale { get; set; } = 1;

    /// <summary>
    /// Which of the view's sizes a texture follows: the size it is drawn at, or the size it is shown at, which
    /// upscaling fills.
    /// </summary>
    public PassSize Size { get; set; }

    /// <summary>
    /// It is kept from frame to frame as two: what the passes write this frame by its name, and what they wrote the
    /// frame before as <c>NameHistory</c>, which they read; the two swap each frame, and the history starts empty
    /// whenever the texture is made again, as when the view changes size.
    /// </summary>
    public bool History { get; set; }

    public uint Width { get; set; }

    public uint Height { get; set; }

    /// <summary>
    /// Mip levels, each half the one before.
    /// </summary>
    public uint Levels { get; set; } = 1;

    /// <summary>
    /// A texture array's layers, one for each iteration of a pass that writes it for each of a count.
    /// </summary>
    public uint Layers { get; set; } = 1;

    /// <summary>
    /// The texture is a cube, its six layers its faces, +X, -X, +Y, -Y, +Z, -Z.
    /// </summary>
    public bool Cube { get; set; }

    /// <summary>
    /// Texels deep: the texture is a volume, <see cref="Width"/> by <see cref="Height"/> by this; 0 for a flat one.
    /// </summary>
    public uint Depth { get; set; }

    /// <summary>
    /// The HLSL type a buffer's rows are, which its shaders declare it as: <c>uint</c>, or a struct the renderer's
    /// headers declare.
    /// </summary>
    public string Type { get; set; } = "uint";

    public uint Stride { get; set; } = 4;

    public uint Rows { get; set; } = 1;

    /// <summary>
    /// A count the renderer knows: the buffer holds that many rows, at least one, rather than <see cref="Rows"/>; a
    /// texture is that many texels a side, rather than <see cref="Width"/> by <see cref="Height"/>.
    /// </summary>
    public string Per { get; set; } = string.Empty;

    /// <summary>
    /// A count the renderer knows: the buffer holds a copy of its rows for each, one per iteration of the passes that
    /// run for each of that count, or room for every one of them at once.
    /// </summary>
    public string Copies { get; set; } = string.Empty;

    /// <summary>
    /// A buffer the renderer has whose bytes are copied into each copy of this one at the start of its stage, every
    /// frame: the draw arguments a culling pass counts into, say.
    /// </summary>
    public string Seed { get; set; } = string.Empty;
}

/// <summary>
/// How many groups a compute <see cref="Pass"/> runs: enough for one thread per pixel of the view (<c>view</c>), per
/// texel of a texture it writes (<c>output:Name</c>), or per one of a count the renderer knows (<c>lights</c>); with a
/// <see cref="Tile"/>, one group per tile of that many pixels a side instead; <see cref="Groups"/>, fixed; or as many
/// as a pass before it worked out on the GPU (<see cref="Args"/>).
/// </summary>
public sealed class PassDispatch
{
    public string Per { get; set; } = string.Empty;

    public uint Tile { get; set; }

    public IReadOnlyList<uint> Groups { get; set; } = [];

    /// <summary>
    /// A buffer a pass before it filled with the groups to run, three <c>uint</c>s, x, y and z: work whose amount only
    /// the GPU knows, such as one group for each cluster a cull kept. With <see cref="Pass.Each"/>, each iteration's
    /// are the three after the last's.
    /// </summary>
    public string Args { get; set; } = string.Empty;

    /// <summary>
    /// The first of <see cref="Args"/>' rows this pass runs by, so one buffer holds the arguments of several passes.
    /// </summary>
    public uint First { get; set; }
}

/// <summary>
/// What a draw <see cref="Pass"/> draws: its colour targets in <c>SV_Target</c> order, its depth target, what happens
/// to what they held, and, for meshes, the draw arguments and instances a culling pass made.
/// </summary>
public sealed class PassDraw
{
    public PassDrawn Drawn { get; set; }

    public IReadOnlyList<string> Colors { get; set; } = [];

    public string Depth { get; set; } = string.Empty;

    public GpuContents Contents { get; set; } = GpuContents.Keep;

    /// <summary>
    /// The buffer of indirect draw arguments a culling pass filled, a run of them per surface pipeline.
    /// </summary>
    public string Args { get; set; } = string.Empty;

    /// <summary>
    /// The first of <see cref="Args"/>' rows this pass draws by, so one buffer holds the arguments of several passes.
    /// </summary>
    public uint First { get; set; }

    /// <summary>
    /// The buffer of instances a culling pass filled, which the draws step through.
    /// </summary>
    public string Instances { get; set; } = string.Empty;

    /// <summary>
    /// A depth draw's bias, in the depth format's smallest steps and as a factor of the slope, and the most the slope
    /// may move depth (reverse-Z: negative pushes away from the light); 0 for none.
    /// </summary>
    public float DepthBias { get; set; }

    public float DepthBiasSlope { get; set; }

    public float DepthBiasClamp { get; set; }

    /// <summary>
    /// How a depth draw's depth is tested: <c>always</c> to write over what is there, as emptying a shadow page does.
    /// </summary>
    public GpuCompare DepthCompare { get; set; } = GpuCompare.GreaterOrEqual;

    /// <summary>
    /// Its colour targets' levels past the first are made from what it drew, once it has run.
    /// </summary>
    public bool Mipmaps { get; set; }
}
