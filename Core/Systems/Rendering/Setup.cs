using Magic.Contexts.Assets;
using Magic.Contexts.Rendering;
using Magic.Contexts.Settings;
using Magic.Interfaces;
using Magic.Services;
using Magic.Utils;

namespace Magic.Systems.Rendering;

/// <summary>Builds the <see cref="RenderContext"/> for a renderer: the tables, the built-in shaders and the prewarmed failure pipelines.</summary>
internal static class Setup
{
    /// <summary>A context for <paramref name="gpu"/>. Throws when a built-in shader does not compile: nothing could be drawn.</summary>
    public static RenderContext Create(IRendering gpu, Assets assets, IFileSystem files, Project project)
    {
        GpuStructs.AssertLayout();
        RenderSettings settings = project.Settings.Render;
        Debugging.Log.Info($"Render settings: occlusion {settings.OcclusionCulling}, anisotropy {settings.Anisotropy}, resolution {(settings.Resolution.IsEmpty ? "the window's" : $"{settings.Resolution.Width} x {settings.Resolution.Height}")}.");

        ulong defaultSurface = assets.Find<Shader>("Shaders/Surfaces/Pbr.surf.hlsl").Id;
        ulong failureSurface = assets.Find<Shader>("Shaders/Surfaces/Failure.surf.hlsl").Id;
        if (defaultSurface == 0)
            Debugging.Log.Error("Shaders/Surfaces/Pbr.surf.hlsl is not an indexed asset: there is no default material.");
        if (failureSurface == 0)
            Debugging.Log.Error("Shaders/Surfaces/Failure.surf.hlsl is not an indexed asset: broken materials and shaders will draw grey or not at all.");

        string? cache = settings.ShaderCache ? Path.Combine(project.Cache, "Shaders", gpu.ShaderFormat) : null;
        RenderContext ctx = new()
        {
            Gpu = gpu,
            Assets = assets,
            Files = files,
            Project = project,
            Settings = settings,
            Shaders = new ShaderCache(Path.Combine(project.Resources, "Shaders"), cache, gpu.ShaderFormat),
            Textures = new TextureTable(gpu, settings.Anisotropy),
            Meshes = new MeshTable(gpu, 1 << 20, 1 << 21),
            Materials = new MaterialTable(gpu, defaultSurface, failureSurface),
            Instances = new InstanceTable(gpu, 4096),
            Buckets = new Buckets(gpu, 1 << 22),
            LinearClamp = gpu.CreateSampler(new SamplerDesc(GpuFilter.Linear, GpuAddress.Clamp)),
            NearestClamp = gpu.CreateSampler(new SamplerDesc(GpuFilter.Nearest, GpuAddress.Clamp)),
        };

        Textures.CreateBase(ctx);
        Materials.AddBuiltIn(ctx);
        PipelineClass forced = new(failureSurface, SurfaceVariant.DoubleSided | SurfaceVariant.FailureForced);
        ctx.Pipelines = new PipelineTable(Shaders.CompileBuiltIn(ctx, Pipelines.VertexTemplate, GpuStage.Vertex), forced, FrameTargets.HdrFormat, gpu.DepthFormat);
        ctx.Cull = Culling.Create(ctx);
        ctx.Passes = new PassTable(Shaders.CompileBuiltIn(ctx, "Passes/Fullscreen.vert.hlsl", GpuStage.Vertex));

        // The built-in surfaces compile now, so the first frames of a scripted run draw them, and the failure pipelines are
        // ready before anything can fail.
        Pipelines.Prewarm(ctx, ctx.Materials.PlaceholderClass);
        if (failureSurface != 0)
        {
            Pipelines.Prewarm(ctx, forced);
            Pipelines.Prewarm(ctx, new PipelineClass(failureSurface, SurfaceVariant.None));
        }

        if (gpu.Debug)
            RenderChecks.Probes(ctx);

        Passes.Discover(ctx);
        return ctx;
    }
}
