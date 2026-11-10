using System.Collections.Immutable;
using Magic.Assets;
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
public sealed class GlobalIlluminationGem : IGem, IRender<TracedInput, IndirectLight>, IHookIntoDomainLoading
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
    /// The picture's size the history was last traced at; none while it holds nothing of the world. Render thread.
    /// </summary>
    private (int Width, int Height)? _traced;

    public GlobalIlluminationGem(AssetManager assets, Threads threads, IGraphicsApi gpu)
    {
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
        Result<GpuBuffer> constants = graph.Buffer(GpuDescriptions.GetBuffer("GI constants", GIConstants.Bytes));
        if (!constants.Ok)
        {
            Log.Error($"The indirect light is not traced: {constants.Error}");

            return null;
        }

        GIView view = new(
            graph,
            gpu.Frame,
            constants.Value,
            gpu.Width,
            gpu.Height,
            HasHistory: _traced == (gpu.Width, gpu.Height),
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
        foreach (Result made in (ReadOnlySpan<Result>)[probes, diffuse, reflections])
        {
            if (!made.Ok)
            {
                Log.Error($"The indirect light is not traced: {made.Error}");

                return null;
            }
        }

        graph.Upload(
            view.Constants,
            [new GIConstants { Diffuse = diffuse.Value.Constants, Reflections = reflections.Value.Constants }]
        );
        GpuIndirectLight row = new()
        {
            Diffuse = diffuse.Value.Light.Id,
            Reflections = reflections.Value.Light.Id,
            Grid = probes.Value.Constants,
        };
        graph.Upload(gpu.Frame, [row], GpuIndirectLight.Offset);
        _probes.Record(view, probes.Value);
        _diffuse.Record(view, probes.Value, diffuse.Value);
        _reflections.Record(view, probes.Value, reflections.Value);
        _traced = (gpu.Width, gpu.Height);

        return new IndirectLight(diffuse.Value.Light, reflections.Value.Light, probes.Value.Probes, probes.Value.Depth);
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
}
