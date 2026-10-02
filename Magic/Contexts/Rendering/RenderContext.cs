using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Contexts.Settings;
using Magic.Interfaces;

namespace Magic.Contexts.Rendering;

/// <summary>The culling compute pipelines; the late-pass ones are 0 without occlusion.</summary>
internal sealed record CullPipelines(GpuPipeline PageCull, GpuPipeline CullEarly, GpuPipeline SeedLate, GpuPipeline CullLate, GpuPipeline HiZBuild);

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

    /// <summary>The project's, read as they are every frame; a change to one the context is built from builds a new context.</summary>
    public required RenderSettings Settings { get; init; }

    public required ShaderCache Shaders { get; init; }

    public required TextureTable Textures { get; init; }

    public required MeshTable Meshes { get; init; }

    public required MaterialTable Materials { get; init; }

    public required InstanceTable Instances { get; init; }

    public required Buckets Buckets { get; init; }

    /// <summary>Set while the context is built, once the built-in shaders compiled (they need the rest of it).</summary>
    public PipelineTable Pipelines { get; set; } = null!;

    public PassTable Passes { get; set; } = null!;

    public CullPipelines Cull { get; set; } = null!;

    public required GpuSampler LinearClamp { get; init; }

    public required GpuSampler NearestClamp { get; init; }

    public Dictionary<RenderTarget, FrameTargets> Targets { get; } = [];

    /// <summary>By the view's index in the frame's view list.</summary>
    public List<ViewBuffers> Views { get; } = [];

    /// <summary>Windows a camera targets that are not open, warned about once each.</summary>
    public HashSet<RenderTarget> MissingTargets { get; } = [];

    /// <summary>What a resized pyramid is cleared with; grows, never shrinks.</summary>
    public float[] Zeros { get; set; } = [];
}
