using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Magic.Assets;
using Magic.Configuration;
using Magic.Debugging;
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
    /// The frame's indirect light, while the passes' shaders are built, what they read and write is made, and rays see
    /// an instance; none otherwise, and the surfaces take the sky's light.
    /// </summary>
    public IndirectLight? Render(in Frame frame, RenderGraph graph, in TracedInput input)
    {
        // The shaders are the GPU provider's to build, the first ask starting each; one that fails holds no domain back.
        GpuState shaders = GetState(graph, _shaders.AsSpan());
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
            GpuDescriptions.GetBuffer("GI constants", Unsafe.SizeOf<GIConstants>())
        );
        if (!constants.Ok)
        {
            Log.Error($"The indirect light is not traced: {constants.Error}");

            return null;
        }

        float scale = Settings.IndirectScale;
        (int Width, int Height) tracedSize = (Scale(gpu.Width, scale), Scale(gpu.Height, scale));
        GIView view = new(
            graph,
            gpu.Frame,
            constants.Value,
            tracedSize,
            HasHistory: _traced == tracedSize,
            gpu.Depth,
            gpu.GBufferNormal,
            gpu.GBufferSurface,
            gpu.Velocity,
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
        Result<ProbeBuffers> probes = _probes.Prepare(graph, _traced is not null, input.Traced.Box);
        Result<TracedDiffuseBuffers> diffuse = TracedDiffuse.Prepare(view, frame.Index);
        Result<TracedReflectionBuffers> reflections = TracedReflections.Prepare(view, frame.Index);
        Result<GpuSampler> sampler = graph.Sampler(
            new SamplerDescription { Wrap = TextureWrap.Clamp, MipFilter = TextureFilter.Nearest }
        );
        foreach (Result made in (ReadOnlySpan<Result>)[probes, diffuse, reflections, sampler])
        {
            if (!made.Ok)
            {
                Log.Error($"The indirect light is not traced: {made.Error}");

                return null;
            }
        }

        graph.Upload(
            view.Constants,
            [
                new GIConstants
                {
                    Diffuse = diffuse.Value.Constants,
                    Reflections = reflections.Value.Constants,
                    Sampler = sampler.Value.Id,
                    HasHistory = view.HasHistory ? 1u : 0u,
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
        _reflections.Record(view, probes.Value, reflections.Value);
        _traced = tracedSize;

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

    /// <summary>
    /// A side of the picture at <paramref name="scale"/>, from 1 pixel up to the whole side.
    /// </summary>
    private static int Scale(int side, float scale) => Math.Clamp((int)MathF.Round(side * scale), 1, side);

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
}
