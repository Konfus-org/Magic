using Magic.Contexts.Rendering;
using Magic.Contexts.Components;
using Magic.Contexts.Settings;
using Magic.Interfaces;

namespace DeferredRendererGem;

/// <summary>
/// Everything the host's render system keeps for one renderer: the GPU tables (meshes, textures, materials, instances, draw
/// groups), the shader text and pipelines, the pipeline's passes and what they made, each render target's textures and each view's buffers. Built when a renderer appears and dropped whole when it changes (a reload): its handles
/// mean nothing to another one. State only; <c>Systems/Rendering</c> does the work.
/// </summary>
internal sealed class RenderContext
{
    public required IRendering Gpu { get; init; }

    public required Magic.Services.Assets Assets { get; init; }

    public required IFileSystem Files { get; init; }

    public required Magic.Services.Project Project { get; init; }

    /// <summary>
    /// Where the compiles run: its workers.
    /// </summary>
    public required Magic.Services.Threads Threads { get; init; }

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

    /// <summary>
    /// The pipeline the frame follows and every pass it names.
    /// </summary>
    public PipelineState Pipeline { get => field ?? throw new InvalidOperationException("The render context is read before it is built."); set; }

    /// <summary>
    /// Every name a pass reads or writes, and what the passes created behind the names.
    /// </summary>
    public ResourceRegistry Resources { get; } = new();

    /// <summary>
    /// The occupancy bricks of the meshes in use, and the atlas that holds them (<c>BrickAtlas</c> to a pass).
    /// </summary>
    public GiBricks Bricks { get; } = new();

    public required GpuTexture BrickAtlas { get; init; }

    /// <summary>
    /// This frame's brick jobs (<see cref="GpuBrickJob"/> rows) and their indirect dispatches, uploaded at Plan for the
    /// passes that run once per job.
    /// </summary>
    public required GrowableBuffer BrickJobs { get; init; }

    public required GrowableBuffer BrickArgs { get; init; }

    /// <summary>
    /// An 8 x 8 x 8 volume bound in place of one a pass has not made.
    /// </summary>
    public required GpuTexture StubVolume { get; init; }

    /// <summary>
    /// The frame's point and spot lights, one <see cref="GpuLight"/> each, uploaded whole every frame.
    /// </summary>
    public required GrowableBuffer Lights { get; init; }

    /// <summary>
    /// The frame's glows, one <see cref="GpuGlow"/> each, uploaded whole every frame; <see cref="GlowCount"/> of them.
    /// </summary>
    public required GrowableBuffer Glows { get; init; }

    public uint GlowCount { get; set; }

    /// <summary>
    /// This frame's counts (<see cref="GpuCounts"/>), uploaded whole every frame for the passes that size their work by them.
    /// </summary>
    public required GrowableBuffer Counts { get; init; }

    /// <summary>
    /// A 1 x 1 depth texture and a 16-byte buffer, bound in place of what a pass has not made (a shadow atlas while no
    /// shadow pass runs), so every binding is always there.
    /// </summary>
    public required GpuTexture StubDepth { get; init; }

    public required GpuBuffer StubBuffer { get; init; }

    public required GpuSampler LinearClamp { get; init; }

    public required GpuSampler NearestClamp { get; init; }

    /// <summary>
    /// What a pass reads a shadow map with: a comparison against the fragment's depth, the closer side winning.
    /// </summary>
    public required GpuSampler Comparison { get; init; }

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
    /// Windows a camera targets that are not open, warned about once each.
    /// </summary>
    public HashSet<RenderTarget> MissingTargets { get; } = [];
}
