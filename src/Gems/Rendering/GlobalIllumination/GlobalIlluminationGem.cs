using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Magic.Assets;
using Magic.Configuration;
using Magic.Debugging;
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
/// Lights the view's surfaces indirectly, through the world as rays see it: the probe grid, the traced diffuse light and
/// the traced reflections, recorded in the light stage, written into the frame's buffer and made as the frame's
/// <see cref="IndirectLight"/>, which shading takes. It is public because nothing in its assembly makes it: Magic does,
/// by reflection, when a project lists the gem.
/// </summary>
public sealed class GlobalIlluminationGem
    : IGem,
        IHasSettings<GlobalIlluminationSettings>,
        IRender<TracedInput, IndirectLight>,
        IHookIntoDomainLoading
{
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
    /// The size the history was last traced at; none while it holds nothing of the world. Render thread.
    /// </summary>
    private (int Width, int Height)? _traced;

    /// <summary>
    /// The size the reflections were last traced at; none while they are off. Render thread.
    /// </summary>
    private (int Width, int Height)? _reflected;

    public GlobalIlluminationGem(
        GlobalIlluminationSettings settings,
        AssetManager assets,
        Threads threads,
        IGraphicsApi gpu
    )
    {
        Settings = settings;
        _threads = threads;
        _probes = new Probes(assets);
        _diffuse = new TracedDiffuse(assets);
        _reflections = new TracedReflections(assets);
        _shaders = [.. _probes.Shaders, .. _diffuse.Shaders, .. _reflections.Shaders];

        // Without rays nothing is traced and the gem is never called, so it holds no domain back.
        if (!gpu.Features.HasRayQuery)
            _built.TrySetResult();
    }

    /// <summary>
    /// How the light is traced, which <see cref="Render"/> reads whole each frame. Game thread.
    /// </summary>
    public GlobalIlluminationSettings Settings { get; set; }

    /// <summary>
    /// The frame's indirect light, while it is on, the passes' shaders are built, what they read and write is made,
    /// and rays see an instance; none otherwise, and the surfaces take the sky's light.
    /// </summary>
    public IndirectLight? Render(in Frame frame, RenderGraph graph, in TracedInput input)
    {
        // Off, no domain waits for the passes, and the probes start afresh once it is back on.
        if (!Settings.IsIndirectLightOn)
        {
            _built.TrySetResult();
            _traced = null;

            return null;
        }

        // The shaders are the GPU provider's to build, the first ask starting each; one that fails holds no domain back.
        GpuState shaders = graph.GetState(_shaders.AsSpan());
        if (shaders != GpuState.Loading)
            _built.TrySetResult();

        if (shaders != GpuState.Ready)
            return null;

        // Where rays see no instance, the probes start afresh once they do.
        if (input.Traced.Structure.IsNone)
        {
            _traced = null;

            return null;
        }

        GpuView gpu = input.View;
        Result<GpuBuffer> constants = graph.Buffer(
            new() { Bytes = Unsafe.SizeOf<GIConstants>(), Name = "GI constants" }
        );
        (int Width, int Height) tracedSize = gpu.Scaled(Settings.IndirectScale);
        GIView view = new(
            graph,
            gpu,
            constants.Value,
            tracedSize,
            HasHistory: _traced == tracedSize,
            [
                gpu.SkyTransmittance,
                gpu.SkyMultiScattering,
                gpu.SkyView,
                gpu.LightTable,
                gpu.Instances,
                gpu.Vertices,
                gpu.Indices,
                gpu.Meshes,
                input.Traced.HitMaterials,
            ]
        );
        Result<ProbeBuffers> probes = _probes.Prepare(
            graph,
            _traced is not null,
            input.Traced.Box,
            (Settings.ProbesAcross, Settings.ProbesUp)
        );
        Result<TracedDiffuseConstants> diffuse = TracedDiffuse.Prepare(view, frame.Index);
        Result<TracedReflectionConstants> reflections = Settings.IsReflectionsOn
            ? TracedReflections.Prepare(
                view with
                {
                    HasHistory = view.HasHistory && _reflected == tracedSize,
                },
                frame.Index
            )
            : Result.Success(default(TracedReflectionConstants));
        Result<GpuSampler> sampler = graph.Sampler(
            new SamplerDescription { Wrap = TextureWrap.Clamp, MipFilter = TextureFilter.Nearest }
        );
        if (Result.FindFailure(constants, probes, diffuse, reflections, sampler) is { Ok: false } failed)
        {
            Log.Error($"The indirect light is not traced: {failed.Error}");

            return null;
        }

        graph.Upload(
            view.Constants,
            [
                new GIConstants
                {
                    Diffuse = diffuse.Value,
                    Reflections = reflections.Value,
                    Sampler = sampler.Value,
                    TracedWidth = (uint)tracedSize.Width,
                    TracedHeight = (uint)tracedSize.Height,
                },
            ]
        );
        GpuTexture diffuseLight = diffuse.Value.Blend.Light;
        GpuTexture reflectionLight = reflections.Value.Blend.Light;
        GpuIndirectLight row = new()
        {
            Diffuse = diffuseLight.Id,
            Reflections = reflectionLight.Id,
            Grid = probes.Value.Constants,
        };
        graph.Upload(gpu.Frame, [row], GpuIndirectLight.Offset);
        _probes.Record(view, probes.Value);
        _diffuse.Record(view, probes.Value, diffuse.Value);
        if (Settings.IsReflectionsOn)
            _reflections.Record(view, probes.Value, reflections.Value);

        _traced = tracedSize;
        _reflected = Settings.IsReflectionsOn ? tracedSize : null;

        return new IndirectLight(diffuseLight, reflectionLight, probes.Value.Probes, probes.Value.Depth);
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
