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
/// What the indirect light takes each frame: the view, the world as rays see it and the sky's tables, each null in a
/// frame that lacks it.
/// </summary>
public readonly record struct GlobalIlluminationInput(GpuView? View, TracedWorld? Traced, SkyTables? Sky);

/// <summary>
/// Lights each view's surfaces indirectly through the world as rays see it, as the view's
/// <see cref="IndirectLight"/>.
/// </summary>
/// <remarks>
/// It uses the probe grid, the traced diffuse light and the traced reflections, and writes into the view's frame
/// buffer. Shading takes the indirect light. Each view has its own grid and history.
/// </remarks>
public sealed class Renderer : IRender<GlobalIlluminationInput, IndirectLight>, IHookIntoDomainLoading
{
    /// <summary>
    /// What a view takes while it settles, after its probes start afresh: the most diffuse rays, a history that keeps
    /// 0.8 a frame, so 0.8^14 &lt; 5% of the start is left after 14 frames, and probes that take half of their rays'
    /// light.
    /// </summary>
    private const int SettleRayCount = 8; // PROTO
    private const float SettleHistory = 0.8f; // PROTO
    private const int SettleHistoryFrames = 14; // PROTO
    private const float SettleProbeBlend = 0.5f; // PROTO

    /// <summary>
    /// How many times each probe gets its first rays before the probes count as settled: the first gives them the
    /// light that bounced once, the second the light that bounced off what the first lit.
    /// </summary>
    private const int SettleProbeSweeps = 2; // PROTO

    /// <summary>
    /// The view depth's two textures: the even frames write the first and the odd the second, the other being the
    /// history both blends read.
    /// </summary>
    private static readonly string[] _depthNames = ["Traced depth 0", "Traced depth 1"];

    /// <summary>
    /// How the light is traced, which <see cref="Render"/> reads each frame.
    /// </summary>
    private readonly GlobalIlluminationSettings _settings;
    private readonly Probes _probes;
    private readonly TracedDiffuse _diffuse;
    private readonly TracedReflections _reflections;
    private readonly ImmutableArray<ShaderEntry> _shaders;

    /// <summary>
    /// The open sky, which the traces are specialised on in a frame without the sky's tables.
    /// </summary>
    private readonly ImmutableArray<ShaderTypeArgument> _openSky;

    /// <summary>
    /// Ends once the passes' shaders are built or failed, once a frame with a view has no world as rays see it, or on
    /// a GPU that doesn't trace rays.
    /// </summary>
    /// <remarks>
    /// A domain loading waits for it, so its first frame is lit as the ones after.
    /// </remarks>
    private readonly TaskCompletionSource _built = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// The wait of the world that arrived for the indirect light to settle, which the frame that settles the first
    /// view or traces nothing for good completes. Render thread.
    /// </summary>
    private readonly SettleWait _settling;

    /// <summary>
    /// For each view, by its number: the size its history was last traced at, none while it holds nothing of the
    /// world, the size its reflections were last traced at, none while they are off, and the frames its history blended
    /// since the probes started afresh. Render thread.
    /// </summary>
    private readonly List<((int Width, int Height)? Traced, (int Width, int Height)? Reflected, int Blended)> _views =
    [];

    internal Renderer(GlobalIlluminationSettings settings, AssetManager assets, Threads threads, IGraphicsApi gpu)
    {
        _settings = settings;
        _settling = new SettleWait(threads);
        _probes = new Probes(assets);
        _diffuse = new TracedDiffuse(assets);
        _reflections = new TracedReflections(assets);
        _openSky = SkyTables.GetOpenSky(assets);
        _shaders =
        [
            .. _probes.Shaders,
            .. _diffuse.Shaders,
            .. _reflections.Shaders,
            _probes.TraceShader with
            {
                TypeArguments = _openSky,
            },
            _diffuse.TraceShader with
            {
                TypeArguments = _openSky,
            },
            _reflections.TraceShader with
            {
                TypeArguments = _openSky,
            },
        ];

        // Without rays nothing is traced, so the renderer holds no domain back.
        if (!gpu.Features.HasRayQuery)
            _built.TrySetResult();
    }

    /// <summary>
    /// Traces the indirect light of the graph's view and writes its row into the view's frame buffer.
    /// </summary>
    /// <returns>
    /// The view's indirect light, or none while it is off, the shaders build, what they read and write is not made, or
    /// rays see no instance. With none, the surfaces take the sky's light.
    /// </returns>
    public IndirectLight? Render(in Frame frame, RenderGraph graph, in GlobalIlluminationInput input)
    {
        bool isReflectionsOn = _settings.IsReflectionsOn;

        // What the view keeps from frame to frame, by its number.
        while (_views.Count <= graph.View)
        {
            _views.Add(default);
        }

        // Off, no domain waits for the passes, and the probes start afresh once it is back on.
        if (!_settings.IsIndirectLightOn)
        {
            _built.TrySetResult();
            _settling.End();
            _views[graph.View] = default;

            return null;
        }

        // A frame without a view because its maker is out lets the loading domain show: the failure is already told.
        // Otherwise the view is still loading, and the domain keeps waiting for the passes.
        if (input.View is not { } gpu)
        {
            if (graph.IsMakerOut<GpuView>())
            {
                _built.TrySetResult();
                _settling.End();
            }

            return null;
        }

        // With a view, the world as rays see it is lacking where no gem makes it, rays are not traced, or its maker
        // failed and told so, and in a frame its maker did not build it: nothing is traced, no domain waits, and the
        // probes start afresh once it is back. Only the first three end the settling.
        if (input.Traced is not { } traced)
        {
            _built.TrySetResult();
            if (!graph.Features.HasRayQuery || graph.IsMakerOut<TracedWorld>())
                _settling.End();

            _views[graph.View] = default;

            return null;
        }

        // The shaders are the GPU provider's to build, the first ask starting each; one that fails holds no domain back.
        // The traces on the sky's type and on the open sky are both built, as a domain's frames take either.
        ImmutableArray<ShaderTypeArgument> skyType = input.Sky?.SkyType ?? _openSky;
        GpuState shaders = graph.GetState(_shaders.AsSpan());
        GpuState skyShaders = graph.GetState(
            _probes.TraceShader with
            {
                TypeArguments = skyType,
            },
            _diffuse.TraceShader with
            {
                TypeArguments = skyType,
            },
            _reflections.TraceShader with
            {
                TypeArguments = skyType,
            }
        );
        if (skyShaders != GpuState.Ready && shaders != GpuState.Failed)
            shaders = skyShaders;

        if (shaders != GpuState.Loading)
            _built.TrySetResult();

        if (shaders == GpuState.Failed)
        {
            _settling.End();
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
            _settling.End();
            _views[graph.View] = default;

            return null;
        }

        Result<GpuBuffer> constants = graph.Buffer(new("GI constants", Unsafe.SizeOf<GIConstants>()));
        (int Width, int Height) tracedSize = gpu.Scaled(_settings.IndirectScale);

        // A view that lost its history at the same size draws another world: its probes and history start afresh
        // at that world's first frame.
        if (!gpu.HasHistory && _views[graph.View].Traced == tracedSize)
            _views[graph.View] = default;

        ((int Width, int Height)? lastTraced, (int Width, int Height)? lastReflected, int lastBlended) = _views[
            graph.View
        ];
        bool hasHistory = lastTraced == tracedSize;
        ReadOnlySpan<GpuResource> sky = input.Sky is { HasTables: true } tables
            ? [tables.Transmittance, tables.View]
            : [];

        // A new traced size starts the history afresh, but not the probes, which settle from where they are.
        int blended = lastTraced is null ? 0 : lastBlended + 1;
        int settleFrames = GetSettleFrames();
        bool isSettling = blended < settleFrames;
        GIView view = new(
            graph,
            gpu,
            constants.Value,
            tracedSize,
            [.. sky, gpu.LightTable, gpu.Instances, gpu.Vertices, gpu.Indices, gpu.Meshes, traced.HitMaterials],
            SkyTables.GetSkyType(input.Sky, _openSky)
        );
        Result<(ProbeGridConstants Grid, ProbeUpdateConstants Update)> probes = _probes.Prepare(
            graph,
            lastTraced is not null,
            traced.Box,
            (_settings.ProbesAcross, _settings.ProbesUp)
        );
        Result<TracedDiffuseConstants> diffuse = TracedDiffuse.Prepare(view, frame.Index, hasHistory);
        Result<TracedReflectionConstants> reflections = isReflectionsOn
            ? TracedReflections.Prepare(view, frame.Index, hasHistory && lastReflected == tracedSize)
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
            _settling.End();
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
            DiffuseRayCount = (uint)Math.Clamp(isSettling ? SettleRayCount : _settings.IndirectRayCount, 1, 8),
            DiffuseRayRange = Math.Max(_settings.IndirectRayRange, 0),
            ReflectionRayRange = Math.Max(_settings.ReflectionRayRange, 0),
            DiffuseHistory = Math.Clamp(
                isSettling ? Math.Min(SettleHistory, _settings.IndirectHistory) : _settings.IndirectHistory,
                0,
                1
            ),
            ReflectionHistory = Math.Clamp(
                isSettling ? Math.Min(SettleHistory, _settings.ReflectionHistory) : _settings.ReflectionHistory,
                0,
                1
            ),
            ProbeBlend = Math.Clamp(
                isSettling ? Math.Max(SettleProbeBlend, _settings.ProbeUpdateSpeed) : _settings.ProbeUpdateSpeed,
                0,
                1
            ),
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

        (int Width, int Height)? reflected = isReflectionsOn ? tracedSize : null;
        _views[graph.View] = (tracedSize, reflected, blended);
        if (graph.View == 0 && blended >= settleFrames)
            _settling.End();

        return new IndirectLight(gi.Diffuse.Blend.Light, gi.Reflections.Blend.Light, grid.Probes, grid.Depth);
    }

    public Task OnDomainLoadingAsync(ImmutableArray<ImmutableArray<IComponent>> entities, CancellationToken cancel) =>
        _built.Task.WaitAsync(cancel);

    /// <summary>
    /// Returns once the first view was traced for <see cref="GetSettleFrames"/> frames since its probes started afresh,
    /// or traces nothing for good: no view, no rays, indirect light off, a pass that failed, or no instance that rays
    /// see. It waits while the world as rays see it or the shaders are not built yet.
    /// </summary>
    public Task OnDomainArrivedAsync(CancellationToken cancel) => _settling.WaitAsync(cancel);

    /// <summary>
    /// The frames a view takes to settle after its probes start afresh: as many as its probes need to get their first
    /// rays <see cref="SettleProbeSweeps"/> times, and at least as many as its history needs.
    /// </summary>
    private int GetSettleFrames() =>
        Math.Max(
            SettleHistoryFrames,
            SettleProbeSweeps * Probes.GetSweepFrames((_settings.ProbesAcross, _settings.ProbesUp))
        );
}
