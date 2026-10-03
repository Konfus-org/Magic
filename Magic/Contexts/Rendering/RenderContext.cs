using Magic.Contexts.Components;
using Magic.Contexts.Settings;
using Magic.Interfaces;

namespace Magic.Contexts.Rendering;

/// <summary>
/// The culling compute pipelines; the late-pass ones are 0 without occlusion.
/// </summary>
internal sealed record CullPipelines(GpuPipeline PageCull, GpuPipeline CullEarly, GpuPipeline SeedLate, GpuPipeline CullLate, GpuPipeline HiZBuild);

/// <summary>
/// The lighting compute pipelines: the two that bin the lights, into screen tiles and then into the tiles' depth
/// slices, and the one that shades the gbuffer.
/// </summary>
internal sealed record LightingPipelines(GpuPipeline LightCull, GpuPipeline LightCluster, GpuPipeline Shade);

/// <summary>
/// Everything the host's render system keeps for one renderer: the GPU tables (meshes, textures, materials, instances, draw
/// groups), the shader text and pipelines, the custom passes, each render target's textures and each view's buffers. Built when a renderer appears and dropped whole when it changes (a reload): its handles
/// mean nothing to another one. State only; <c>Systems/Rendering</c> does the work.
/// </summary>
internal sealed class RenderContext
{
    public required IRendering Gpu { get; init; }

    public required Services.Assets Assets { get; init; }

    public required IFileSystem Files { get; init; }

    public required Services.Project Project { get; init; }

    /// <summary>
    /// Where the compiles run: its workers.
    /// </summary>
    public required Services.Threads Threads { get; init; }

    /// <summary>
    /// The project's, read as they are every frame; a change to one the context is built from builds a new context.
    /// </summary>
    public required RenderSettings Settings { get; init; }

    public required ShaderCache Shaders { get; init; }

    public required TextureTable Textures { get; init; }

    public required MeshTable Meshes { get; init; }

    public required MaterialTable Materials { get; init; }

    public required InstanceTable Instances { get; init; }

    public required Buckets Buckets { get; init; }

    /// <summary>
    /// Set while the context is built, once the built-in shaders compiled (they need the rest of it).
    /// </summary>
    public PipelineTable Pipelines { get => field ?? throw new InvalidOperationException("The render context is read before it is built."); set; }

    public PassTable Passes { get => field ?? throw new InvalidOperationException("The render context is read before it is built."); set; }

    public CullPipelines Cull { get => field ?? throw new InvalidOperationException("The render context is read before it is built."); set; }

    public LightingPipelines Lighting { get => field ?? throw new InvalidOperationException("The render context is read before it is built."); set; }

    /// <summary>
    /// The frame's point and spot lights, one <see cref="GpuLight"/> each, uploaded whole every frame.
    /// </summary>
    public required GrowableBuffer Lights { get; init; }

    /// <summary>
    /// The frame's glows, one <see cref="GpuGlow"/> each, uploaded whole every frame; <see cref="GlowCount"/> of them.
    /// </summary>
    public required GrowableBuffer Glows { get; init; }

    public uint GlowCount { get; set; }

    public GpuPipeline GlowPipeline { get; set; }

    public required GpuSampler LinearClamp { get; init; }

    public required GpuSampler NearestClamp { get; init; }

    /// <summary>
    /// Assets the tables are about to ask for, loading off the render thread, by id.
    /// </summary>
    public Pending<ulong, Preloaded> Preloads { get; } = new();

    /// <summary>
    /// The ones that arrived: pooled, so a table that asks for one does not wait.
    /// </summary>
    public Dictionary<ulong, Preloaded> Preloaded { get; } = [];

    /// <summary>
    /// What the tables are handed the assets they ask for from, while one of them works with what arrived.
    /// </summary>
    public List<Preloaded> Using { get; } = [];

    /// <summary>
    /// Assets a table asked for that had not been loaded ahead, said once each.
    /// </summary>
    public HashSet<ulong> Missed { get; } = [];

    /// <summary>
    /// True while the context is being built: the one time the render thread loads what it needs itself and waits
    /// for it (the engine's own shaders, the built-in materials), since no frame can be drawn before it is done.
    /// </summary>
    public bool Building { get; set; } = true;

    /// <summary>
    /// Loaded assets whose file changed (and passes just listed), loading again before the change is applied.
    /// </summary>
    public Pending<ulong, Preloaded> Reloads { get; } = new();

    public Dictionary<RenderTarget, FrameTargets> Targets { get; } = [];

    /// <summary>
    /// By the view's index in the frame's view list.
    /// </summary>
    public List<ViewBuffers> Views { get; } = [];

    /// <summary>
    /// Windows a camera targets that are not open, warned about once each.
    /// </summary>
    public HashSet<RenderTarget> MissingTargets { get; } = [];

    /// <summary>
    /// What a resized pyramid is cleared with; grows, never shrinks.
    /// </summary>
    public float[] Zeros { get; set; } = [];
}
