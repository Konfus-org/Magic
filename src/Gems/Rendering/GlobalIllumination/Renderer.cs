using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Magic.Assets;
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
/// What the indirect light takes each frame: the view, the world as rays see it and the sky's tables, each null in a
/// frame that lacks it.
/// </summary>
public readonly record struct GlobalIlluminationInput(GpuView? View, TracedWorld? Traced, SkyTables? Sky);

/// <summary>
/// Lights each view's surfaces indirectly through the world as rays see it, as the view's
/// <see cref="IndirectLight"/>.
/// </summary>
/// <remarks>
/// It uses the probe grid and the traced diffuse light, and writes into the view's frame buffer. Shading takes the indirect light. Each view has its own grid and history.
/// </remarks>
public sealed class Renderer : IRender<GlobalIlluminationInput, IndirectLight>, IHookIntoDomainLoading, IDisposable
{
    /// <summary>
    /// What a view takes while it settles, after its probes start afresh: the most diffuse rays, a sky share that keeps
    /// at most 0.8 of its history a frame, so 0.8^14 &lt; 5% of the start is left after 14 frames, and probes that take
    /// half of their rays' light. NRD keeps the diffuse light's own history.
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
    /// How the light is traced, which <see cref="Render"/> reads each frame.
    /// </summary>
    private readonly GlobalIlluminationSettings _settings;
    private readonly Probes _probes;
    private readonly TracedDiffuse _diffuse;
    private readonly IGraphicsApi _gpu;

    /// <summary>
    /// For each view, by its number, the denoiser that keeps its history. Render thread.
    /// </summary>
    private readonly List<Denoiser> _denoisers = [];
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
    /// world, and the frames its history blended since the probes started afresh. Render thread.
    /// </summary>
    private readonly List<((int Width, int Height)? Traced, int Blended)> _views = [];

    internal Renderer(GlobalIlluminationSettings settings, AssetManager assets, Threads threads, IGraphicsApi gpu)
    {
        _settings = settings;
        _settling = new SettleWait(threads);
        unsafe
        {
            Nrd.LibraryDesc* denoiser = Nrd.NRD.GetLibraryDesc();
            Log.Info($"NRD {denoiser->versionMajor}.{denoiser->versionMinor}.{denoiser->versionBuild}.");
        }

        _gpu = gpu;

        _probes = new Probes(assets);
        _diffuse = new TracedDiffuse(assets);
        _openSky = SkyTables.GetOpenSky(assets);
        _shaders =
        [
            .. _probes.Shaders,
            .. _diffuse.Shaders,
            _probes.TraceShader with
            {
                TypeArguments = _openSky,
            },
            _diffuse.TraceShader with
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
        // What the view keeps from frame to frame, by its number.
        while (_views.Count <= graph.View)
        {
            _views.Add(default);
            _denoisers.Add(new Denoiser(_gpu, _denoisers.Count > 0 ? _denoisers[0] : null));
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

        ((int Width, int Height)? lastTraced, int lastBlended) = _views[graph.View];
        bool hasHistory = lastTraced == tracedSize;

        // A new traced size starts the history afresh, but not the probes, which settle from where they are.
        int blended = lastTraced is null ? 0 : lastBlended + 1;
        int settleFrames = GetSettleFrames();
        bool isSettling = blended < settleFrames;
        GIView view = new(
            graph,
            gpu,
            constants.Value,
            tracedSize,
            GIView.GetWorld(gpu, traced, input.Sky),
            SkyTables.GetSkyType(input.Sky, _openSky)
        );
        Result<(ProbeGridConstants Grid, ProbeUpdateConstants Update)> probes = _probes.Prepare(
            graph,
            lastTraced is not null,
            (_settings.ProbesAcross, _settings.ProbesUp),
            _settings.ProbeSpacing
        );
        Result<DenoiserTextures> nrd = Denoiser.Prepare(view);
        Result<TracedDiffuseConstants> diffuse = nrd.Ok
            ? TracedDiffuse.Prepare(view, nrd.Value, hasHistory)
            : Result.Failure(nrd.Error);
        int written = (int)(frame.Index % 2);
        Result<GpuTexture> depth = graph.Texture(view.GetTraced(GIView.DepthNames[written], GIView.DepthFormat));
        Result<GpuTexture> depthHistory = graph.Texture(
            view.GetTraced(GIView.DepthNames[1 - written], GIView.DepthFormat)
        );
        Result asked = Result.FindFailure(constants, probes, diffuse, depth, depthHistory);
        if (!asked.Ok)
        {
            _settling.End();
            graph.Fail(RenderFailure.Resource, "Indirect light is not traced", asked.Error);

            return null;
        }

        GIConstants gi = new()
        {
            Diffuse = diffuse.Value,
            Probes = probes.Value.Update,
            Depth = depth.Value,
            DepthHistory = depthHistory.Value,
            TracedWidth = (uint)tracedSize.Width,
            TracedHeight = (uint)tracedSize.Height,
            DiffuseRayCount = (uint)Math.Clamp(isSettling ? SettleRayCount : _settings.IndirectRayCount, 1, 8),
            DiffuseRayRange = Math.Max(_settings.IndirectRayRange, 0),
            DiffuseHistory = Math.Clamp(
                isSettling ? Math.Min(SettleHistory, _settings.IndirectHistory) : _settings.IndirectHistory,
                0,
                1
            ),
            ProbeBlend = Math.Clamp(
                isSettling ? Math.Max(SettleProbeBlend, _settings.ProbeUpdateSpeed) : _settings.ProbeUpdateSpeed,
                0,
                1
            ),
            DiffuseTermsOff =
                (_settings.IsHitProbeLightOn ? 0u : 1u)
                | (_settings.IsDiffuseSkyOn ? 0u : 2u)
                | (_settings.IsShadowFillOn ? 0u : 4u)
                | (_settings.IsPixelProbeLightOn ? 0u : 8u),
        };
        // Not staged: nothing is recorded, and the frame's row stays the view's default, which the shaders read as none.
        if (!graph.Upload(view.Constants, [gi]))
            return null;

        ProbeGridConstants grid = probes.Value.Grid;
        GpuIndirectLight row = new()
        {
            Diffuse = nrd.Value.Output.Id,
            SkyShare = gi.Depth.Id,
            Grid = grid,
            TermsOff = gi.DiffuseTermsOff,
        };
        if (!graph.Upload(gpu.Frame, [row], GpuIndirectLight.Offset))
            return null;

        _probes.Record(view, grid, gi.Probes);
        _diffuse.RecordTrace(view, grid, gi);
        Result denoised = _denoisers[graph.View]
            .Record(view, nrd.Value, frame.Index, (float)frame.AppDeltaSec, !hasHistory);
        if (!denoised.Ok)
            graph.Fail(RenderFailure.Resource, "Indirect light is not denoised", denoised.Error);

        _diffuse.RecordSkyShare(view, gi);
        _views[graph.View] = (tracedSize, blended);
        if (graph.View == 0 && blended >= settleFrames)
            _settling.End();

        return new IndirectLight(nrd.Value.Output, gi.Depth, grid.Probes, grid.Depth);
    }

    public void Dispose()
    {
        foreach (Denoiser denoiser in _denoisers)
            denoiser.Dispose();
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
