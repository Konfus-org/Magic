using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Magic.Assets;
using Magic.DeferredRendering;
using Magic.DeferredRendering.View;
using Magic.Ecs;
using Magic.Graphics;
using Magic.Projects;
using Magic.Threading;
using Magic.Utils;
using Magic.Worlds;

namespace Magic.GlobalIllumination;

/// <summary>
/// What the indirect light takes each frame: the view and the world as rays see it, each null in a frame that lacks it.
/// </summary>
public readonly record struct GlobalIlluminationInput(GpuView? View, TracedWorld? Traced);

/// <summary>
/// Lights the view's surfaces indirectly, through the world as rays see it: the probe grid, the traced diffuse light and
/// the traced reflections, recorded in the light stage, written into the frame's buffer and made as the frame's
/// <see cref="IndirectLight"/>, which shading takes.
/// </summary>
public sealed class Renderer : IRender<GlobalIlluminationInput, IndirectLight>, IHookIntoDomainLoading
{
    /// <summary>
    /// The view depth's two textures: the even frames write the first and the odd the second, the other being the
    /// history both blends read.
    /// </summary>
    private static readonly string[] _depthNames = ["Traced depth 0", "Traced depth 1"];

    /// <summary>
    /// How the light is traced, which <see cref="Render"/> reads each frame.
    /// </summary>
    private readonly GlobalIlluminationSettings _settings;
    private readonly Threads _threads;
    private readonly Probes _probes;
    private readonly TracedDiffuse _diffuse;
    private readonly TracedReflections _reflections;
    private readonly ImmutableArray<ShaderEntry> _shaders;

    /// <summary>
    /// Ends once the passes' shaders are built or failed, once a frame with a view has no world as rays see it, or on
    /// a GPU that doesn't trace rays: what a domain loading waits for, so its first frame is lit as the ones after.
    /// </summary>
    private readonly TaskCompletionSource _built = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// The size the history was last traced at; none while it holds nothing of the world. Render thread.
    /// </summary>
    private (int Width, int Height)? _traced;

    /// <summary>
    /// The size the reflections were last traced at; none while they are off. Render thread.
    /// </summary>
    private (int Width, int Height)? _reflected;

    public Renderer(GlobalIlluminationSettings settings, AssetManager assets, Threads threads, IGraphicsApi gpu)
    {
        _settings = settings;
        _threads = threads;
        _probes = new Probes(assets);
        _diffuse = new TracedDiffuse(assets);
        _reflections = new TracedReflections(assets);
        _shaders = [.. _probes.Shaders, .. _diffuse.Shaders, .. _reflections.Shaders];

        // Without rays nothing is traced, so the renderer holds no domain back.
        if (!gpu.Features.HasRayQuery)
            _built.TrySetResult();
    }

    /// <summary>
    /// The frame's indirect light, while it is on, the passes' shaders are built, what they read and write is made,
    /// and rays see an instance; none otherwise, and the surfaces take the sky's light.
    /// </summary>
    public IndirectLight? Render(in Frame frame, RenderGraph graph, in GlobalIlluminationInput input)
    {
        GlobalIlluminationSettings settings = _settings;
        bool isReflectionsOn = settings.IsReflectionsOn;

        // Off, no domain waits for the passes, and the probes start afresh once it is back on.
        if (!settings.IsIndirectLightOn)
        {
            _built.TrySetResult();
            _traced = null;

            return null;
        }

        // A frame without a view because its maker is out lets the loading domain show: the failure is already told.
        // Otherwise the view is still loading, and the domain keeps waiting for the passes.
        if (input.View is not { } gpu)
        {
            if (graph.IsMakerOut<GpuView>())
                _built.TrySetResult();

            return null;
        }

        // With a view, the world as rays see it is lacking only where no gem makes it, rays are not traced, or its
        // maker failed and told so: nothing is traced, no domain waits, and the probes start afresh once it is back.
        if (input.Traced is not { } traced)
        {
            _built.TrySetResult();
            _traced = null;

            return null;
        }

        // The shaders are the GPU provider's to build, the first ask starting each; one that fails holds no domain back.
        GpuState shaders = graph.GetState(_shaders.AsSpan());
        if (shaders != GpuState.Loading)
            _built.TrySetResult();

        if (shaders == GpuState.Failed)
        {
            graph.Fail(
                RenderFailure.Shader,
                "Indirect light is not traced",
                "a shader of the indirect light was not built."
            );
        }

        if (shaders != GpuState.Ready)
            return null;

        // Where rays see no instance, the probes start afresh once they do.
        if (traced.Structure.IsNone)
        {
            _traced = null;

            return null;
        }

        Result<GpuBuffer> constants = graph.Buffer(new("GI constants", Unsafe.SizeOf<GIConstants>()));
        (int Width, int Height) tracedSize = gpu.Scaled(settings.IndirectScale);
        bool hasHistory = _traced == tracedSize;
        GIView view = new(
            graph,
            gpu,
            constants.Value,
            tracedSize,
            [
                gpu.SkyTransmittance,
                gpu.SkyView,
                gpu.LightTable,
                gpu.Instances,
                gpu.Vertices,
                gpu.Indices,
                gpu.Meshes,
                traced.HitMaterials,
            ]
        );
        Result<(ProbeGridConstants Grid, ProbeUpdateConstants Update)> probes = _probes.Prepare(
            graph,
            _traced is not null,
            traced.Box,
            (settings.ProbesAcross, settings.ProbesUp)
        );
        Result<TracedDiffuseConstants> diffuse = TracedDiffuse.Prepare(view, frame.Index, hasHistory);
        Result<TracedReflectionConstants> reflections = isReflectionsOn
            ? TracedReflections.Prepare(view, frame.Index, hasHistory && _reflected == tracedSize)
            : Result.Success(default(TracedReflectionConstants));
        int written = (int)(frame.Index % 2);
        Result<GpuTexture> depth = graph.Texture(view.GetTraced(_depthNames[written], GpuFormat.R32Sfloat));
        Result<GpuTexture> depthHistory = graph.Texture(view.GetTraced(_depthNames[1 - written], GpuFormat.R32Sfloat));
        Result<GpuSampler> sampler = graph.Sampler(
            new SamplerDescription { Wrap = TextureWrap.Clamp, MipFilter = TextureFilter.Nearest }
        );
        Result asked = Result.FindFailure(constants, probes, diffuse, reflections, depth, depthHistory, sampler);
        if (!asked.Ok)
        {
            graph.Fail(RenderFailure.Resource, "Indirect light is not traced", asked.Error);

            return null;
        }

        GIConstants gi = new()
        {
            Diffuse = diffuse.Value,
            Reflections = reflections.Value,
            Probes = probes.Value.Update,
            Depth = depth.Value,
            DepthHistory = depthHistory.Value,
            Sampler = sampler.Value,
            TracedWidth = (uint)tracedSize.Width,
            TracedHeight = (uint)tracedSize.Height,
        };
        // Not staged: nothing is recorded, and the frame's row stays the view's default, which the shaders read as none.
        if (!graph.Upload(view.Constants, [gi]))
            return null;

        ProbeGridConstants grid = probes.Value.Grid;
        GpuIndirectLight row = new()
        {
            Diffuse = gi.Diffuse.Blend.Light.Id,
            Reflections = gi.Reflections.Blend.Light.Id,
            Grid = grid,
        };
        if (!graph.Upload(gpu.Frame, [row], GpuIndirectLight.Offset))
            return null;

        _probes.Record(view, grid, gi.Probes);
        _diffuse.Record(view, grid, gi);
        if (isReflectionsOn)
            _reflections.Record(view, grid, gi);

        _traced = tracedSize;
        _reflected = isReflectionsOn ? tracedSize : null;

        return new IndirectLight(gi.Diffuse.Blend.Light, gi.Reflections.Blend.Light, grid.Probes, grid.Depth);
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
}
