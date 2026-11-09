using System.Collections.Immutable;
using Magic.Assets;
using Magic.Debugging;
using Magic.Ecs;
using Magic.Projects;
using Magic.Rendering;
using Magic.Threading;
using Magic.Utils;
using Magic.Worlds;

namespace Magic.GlobalIllumination;

/// <summary>
/// On a GPU that traces rays, lights the view's surfaces indirectly: the probe grid, the traced diffuse light and the
/// traced reflections, recorded in the light stage and named by the frame's indirect light, which shading reads. It
/// reads the frame, the view's GBuffer and the world as the renderer shares them, by <see cref="SharedResources"/>. It is
/// public because nothing in its assembly makes it: Magic does, by reflection, when a project lists the gem.
/// </summary>
public sealed class GlobalIlluminationGem : IGem, IHookIntoDomainLoading
{
    private const int HeadlessWidth = 1280;
    private const int HeadlessHeight = 720;

    private readonly Threads _threads;
    private readonly Probes _probes;
    private readonly TracedDiffuse _diffuse;
    private readonly TracedReflections _reflections;
    private readonly ImmutableArray<ShaderEntry> _shaders;

    /// <summary>
    /// Ends once the passes' shaders are built or failed, or on a GPU that doesn't trace rays: what a domain loading
    /// waits for, so its first frame is lit as the ones after.
    /// </summary>
    private readonly TaskCompletionSource _built = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// The picture's size the history was last traced at; none while it holds nothing of the world. Render thread.
    /// </summary>
    private (int Width, int Height)? _traced;

    public GlobalIlluminationGem(AssetManager assets, Threads threads)
    {
        _threads = threads;
        _probes = new Probes(assets);
        _diffuse = new TracedDiffuse(assets);
        _reflections = new TracedReflections(assets);
        _shaders = [.. _probes.Shaders, .. _diffuse.Shaders, .. _reflections.Shaders];
    }

    /// <summary>
    /// The frame's indirect light, while the passes' shaders are built and what they read and write is made.
    /// </summary>
    public void Render(in Frame frame, RenderGraph graph)
    {
        if (!graph.Features.HasRayQuery)
        {
            _built.TrySetResult();

            return;
        }

        // The shaders are the GPU provider's to build, the first ask starting each; one that fails holds no domain back.
        GpuState shaders = GetState(graph, _shaders.AsSpan());
        if (shaders != GpuState.Loading)
            _built.TrySetResult();

        if (shaders != GpuState.Ready)
            return;

        Result<GIView> view = GetView(graph, _traced);
        if (!view.Ok)
        {
            Log.Error($"The indirect light is not traced: {view.Error}");

            return;
        }

        Result<ProbeBuffers> probes = Probes.Prepare(graph, _traced is not null);
        Result<TracedDiffuseBuffers> diffuse = TracedDiffuse.Prepare(view.Value, frame.Index);
        Result<TracedReflectionBuffers> reflections = TracedReflections.Prepare(view.Value, frame.Index);
        foreach (Result made in (ReadOnlySpan<Result>)[probes, diffuse, reflections])
        {
            if (!made.Ok)
            {
                Log.Error($"The indirect light is not traced: {made.Error}");

                return;
            }
        }

        graph.Upload(
            view.Value.Constants,
            [new GIConstants { Diffuse = diffuse.Value.Constants, Reflections = reflections.Value.Constants }]
        );
        graph.Upload(
            view.Value.IndirectLight,
            [
                new IndirectLight
                {
                    FrameNumber = frame.Index + 1,
                    Diffuse = diffuse.Value.Light.Id,
                    Reflections = reflections.Value.Light.Id,
                    Grid = probes.Value.Constants,
                },
            ]
        );
        _probes.Record(view.Value, probes.Value);
        _diffuse.Record(view.Value, probes.Value, diffuse.Value);
        _reflections.Record(view.Value, probes.Value, reflections.Value);
        _traced = (view.Value.Width, view.Value.Height);
    }

    /// <summary>
    /// Forgets what was traced of the world that left, on the render thread between frames, so the probes and the
    /// history start afresh in what comes.
    /// </summary>
    public async Task OnDomainUnloadedAsync(CancellationToken cancel)
    {
        await _threads.Enter(EngineThread.Render);
        _traced = null;
    }

    public Task OnDomainLoadingAsync(ImmutableArray<ImmutableArray<IComponent>> entities, CancellationToken cancel) =>
        _built.Task.WaitAsync(cancel);

    /// <summary>
    /// Where <paramref name="shaders"/> are: failed if any failed, loading while any loads, else ready. Each is asked,
    /// so the first ask starts them all.
    /// </summary>
    private static GpuState GetState(RenderGraph graph, ReadOnlySpan<ShaderEntry> shaders)
    {
        GpuState all = GpuState.Ready;
        foreach (ShaderEntry shader in shaders)
        {
            GpuState state = graph.GetState(shader);
            if (state == GpuState.Failed)
                all = GpuState.Failed;
            else if (state == GpuState.Loading && all == GpuState.Ready)
                all = GpuState.Loading;
        }

        return all;
    }

    /// <summary>
    /// What the passes read and write beyond their own, each asked for, at the picture's size: the window's, else
    /// 1280 by 720 headless, as the renderer picks it.
    /// </summary>
    private static Result<GIView> GetView(RenderGraph graph, (int Width, int Height)? traced)
    {
        TextureDescription window = graph.WindowTarget.IsNone ? default : graph.GetDescription(graph.WindowTarget);
        (int width, int height) = window.Width > 0 ? (window.Width, window.Height) : (HeadlessWidth, HeadlessHeight);
        Result<GpuBuffer> frame = graph.Buffer(SharedResources.Frame);
        Result<GpuBuffer> constants = graph.Buffer(GpuDescriptions.GetBuffer("GI constants", GIConstants.Bytes));
        Result<GpuBuffer> indirect = graph.Buffer(SharedResources.IndirectLight);
        Result<GpuTexture> depth = graph.Texture(GetTarget(SharedResources.Depth));
        Result<GpuTexture> normal = graph.Texture(GetTarget(SharedResources.GBufferNormal));
        Result<GpuTexture> surface = graph.Texture(GetTarget(SharedResources.GBufferSurface));
        Result<GpuTexture> velocity = graph.Texture(GetTarget(SharedResources.Velocity));
        Result<GpuTexture> transmittance = graph.Texture(SharedResources.SkyTransmittance);
        Result<GpuTexture> multiScattering = graph.Texture(SharedResources.SkyMultiScattering);
        Result<GpuTexture> sky = graph.Texture(SharedResources.SkyView);
        Result<GpuBuffer> lights = graph.Buffer(SharedResources.Lights);
        Result<GpuBuffer> instances = graph.Buffer(SharedResources.Instances);
        Result<GpuBuffer> vertices = graph.Buffer(SharedResources.Vertices);
        Result<GpuBuffer> indices = graph.Buffer(SharedResources.Indices);
        Result<GpuBuffer> meshes = graph.Buffer(SharedResources.Meshes);
        Result<GpuBuffer> hitMaterials = graph.Buffer(SharedResources.HitMaterials);
        foreach (
            Result asked in (ReadOnlySpan<Result>)
                [
                    frame,
                    constants,
                    indirect,
                    depth,
                    normal,
                    surface,
                    velocity,
                    transmittance,
                    multiScattering,
                    sky,
                    lights,
                    instances,
                    vertices,
                    indices,
                    meshes,
                    hitMaterials,
                ]
        )
        {
            if (!asked.Ok)
                return Result.Failure(asked.Error);
        }

        return Result.Success(
            new GIView(
                graph,
                frame.Value,
                constants.Value,
                indirect.Value,
                width,
                height,
                HasHistory: traced == (width, height),
                depth.Value,
                normal.Value,
                surface.Value,
                velocity.Value,
                [
                    transmittance.Value,
                    multiScattering.Value,
                    sky.Value,
                    lights.Value,
                    instances.Value,
                    vertices.Value,
                    indices.Value,
                    meshes.Value,
                    hitMaterials.Value,
                ]
            )
        );

        TextureDescription GetTarget(in TextureDescription shared) => shared with { Width = width, Height = height };
    }
}
