using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Magic.Assets;
using Magic.DeferredRendering;
using Magic.DeferredRendering.View;
using Magic.Ecs;
using Magic.GlobalIllumination;
using Magic.Graphics;
using Magic.Threading;
using Magic.Utils;
using Magic.Worlds;

namespace Magic.TracedReflections;

/// <summary>
/// What the reflections take each frame: the view, the world as rays see it, the sky's tables and the indirect light,
/// each null in a frame that lacks it.
/// </summary>
public readonly record struct TracedReflectionsInput(
    GpuView? View,
    TracedWorld? Traced,
    SkyTables? Sky,
    IndirectLight? Light
);

/// <summary>
/// Traces what each smooth surface reflects, on a GPU that traces rays, as the view's <see cref="Reflections"/>.
/// </summary>
/// <remarks>
/// One ray goes from a pixel of each traced pixel's footprint, along a direction that its specular reflects the view
/// into. A light and the probes light it where it lands, and the sky where it lands nowhere. The light is resolved over
/// neighbours and blended over frames into the light that shading reads, at the indirect light's traced size. Each view
/// has its own history.
/// </remarks>
public sealed class Renderer : IRender<TracedReflectionsInput, Reflections>, IHookIntoDomainLoading
{
    /// <summary>
    /// What a view takes while it settles, after its history starts afresh: a history that keeps 0.8 a frame, so
    /// 0.8^14 &lt; 5% of the start is left after 14 frames.
    /// </summary>
    private const float SettleHistory = 0.8f; // PROTO
    private const int SettleFrames = 14; // PROTO

    /// <summary>
    /// The bytes of a <c>ReflectionHit</c> in <c>TraceReflections.slang</c>: 16 words.
    /// </summary>
    private const int HitBytes = 64;

    private static readonly string[] _blendNames = ["Reflection light 0", "Reflection light 1"];

    /// <summary>
    /// Where the reflections' id is in the frame's buffer, in the indirect light's row, which the indirect light writes
    /// as none each frame.
    /// </summary>
    private static readonly ulong _reflectionsOffset = (ulong)(
        GpuIndirectLight.Offset + Marshal.OffsetOf<GpuIndirectLight>(nameof(GpuIndirectLight.Reflections))
    );

    private readonly TracedReflectionsSettings _settings;
    private readonly IGraphicsApi _gpu;
    private readonly GlobalIlluminationSettings _indirect;

    /// <summary>
    /// The trace's entry, generic over the sky, which a frame specialises on its sky.
    /// </summary>
    private readonly ShaderEntry _traceShader;
    private readonly ShaderEntry _resolveShader;

    /// <summary>
    /// The hits' shading, generic over a material shader, and its entries specialised on each of the world's material
    /// shaders by their place. The hits' light is generic over the sky, as the trace is.
    /// </summary>
    private readonly ShaderEntry _shadeShader;
    private readonly List<ShaderEntry> _shadeShaders = [];
    private readonly ShaderEntry _lightShader;
    private readonly ShaderEntry _blendShader;

    /// <summary>
    /// The open sky, which the trace is specialised on in a frame without the sky's tables.
    /// </summary>
    private readonly ImmutableArray<ShaderTypeArgument> _openSky;

    /// <summary>
    /// Ends once the passes' shaders are built or failed, once a frame with a view has no world as rays see it, or on
    /// a GPU that doesn't trace rays.
    /// </summary>
    /// <remarks>
    /// A domain loading waits for it, so its first frame reflects as the ones after.
    /// </remarks>
    private readonly TaskCompletionSource _built = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// The wait of the world that arrived for the reflections to settle, which the frame that settles the first view
    /// or traces nothing for good completes. Render thread.
    /// </summary>
    private readonly SettleWait _settling;

    /// <summary>
    /// For each view, by its number: the size its reflections were last traced at, none while their history holds
    /// nothing of the world, and the frames traced since the history started afresh. Render thread.
    /// </summary>
    private readonly List<((int Width, int Height)? Traced, int Frames)> _reflected = [];

    internal Renderer(
        TracedReflectionsSettings settings,
        GlobalIlluminationSettings indirect,
        AssetManager assets,
        Threads threads,
        IGraphicsApi gpu
    )
    {
        _settings = settings;
        _gpu = gpu;
        _indirect = indirect;
        _settling = new SettleWait(threads);
        _traceShader = PassShaders.GetEntry(assets, "TraceReflections", "TraceReflections");
        _openSky = SkyTables.GetOpenSky(assets);
        _resolveShader = PassShaders.GetEntry(assets, "TraceReflections", "ResolveReflections");
        _shadeShader = PassShaders.GetEntry(assets, "TraceReflections", "ShadeReflectionHits");
        _lightShader = PassShaders.GetEntry(assets, "TraceReflections", "LightReflectionHits");
        _blendShader = PassShaders.GetEntry(assets, "TraceReflections", "BlendReflections");

        // Without rays nothing is traced, so the renderer holds no domain back.
        if (!gpu.Features.HasRayQuery)
            _built.TrySetResult();
    }

    /// <summary>
    /// Traces the reflections of the frame's view and writes their id into the view's frame buffer.
    /// </summary>
    /// <returns>
    /// The view's reflections, or none while they are off, the indirect light is not traced, the shaders build, or
    /// what they read and write is not made. With none, the surfaces reflect the sky, or their diffuse light where it
    /// is traced.
    /// </returns>
    public Reflections? Render(in RenderFrame frame, RenderGraph graph, in TracedReflectionsInput input)
    {
        // What the view keeps from frame to frame, by its number.
        while (_reflected.Count <= frame.View)
        {
            _reflected.Add(default);
        }

        // Off, no domain waits for the passes, and the history starts afresh once it is back on.
        if (!_settings.IsReflectionsOn)
        {
            _built.TrySetResult();
            _settling.End();
            _reflected[frame.View] = default;

            return null;
        }

        // A frame without a view because its maker is out lets the loading domain show: the failure is already told.
        // Otherwise the view is still loading, and the domain keeps waiting for the passes.
        if (input.View is not { } gpu)
        {
            if (frame.IsUnavailable<GpuView>())
            {
                _built.TrySetResult();
                _settling.End();
            }

            return null;
        }

        // With a view, the world as rays see it is lacking where no gem makes it, rays are not traced, or its maker
        // failed and told so, and in a frame its maker did not build it: nothing is traced, no domain waits, and the
        // history starts afresh once it is back. Only the first three end the settling.
        if (input.Traced is not { } traced)
        {
            _built.TrySetResult();
            if (!_gpu.Features.HasRayQuery || frame.IsUnavailable<TracedWorld>())
                _settling.End();

            _reflected[frame.View] = default;

            return null;
        }

        // The shaders are the GPU provider's to build, the first ask starting each; one that fails holds no domain back.
        // The trace on the sky's type and on the open sky are both built, as a domain's frames take either.
        ImmutableArray<ShaderTypeArgument> skyType = input.Sky?.SkyType ?? _openSky;
        GpuState shaders = _gpu.GetState(
            _traceShader with
            {
                TypeArguments = _openSky,
            },
            _lightShader with
            {
                TypeArguments = _openSky,
            },
            _resolveShader,
            _blendShader
        );
        GpuState skyShader = _gpu.GetState(
            _traceShader with
            {
                TypeArguments = skyType,
            },
            _lightShader with
            {
                TypeArguments = skyType,
            }
        );
        if (skyShader != GpuState.Ready && shaders != GpuState.Failed)
            shaders = skyShader;

        GpuState shadeShaders = _gpu.GetState(GetShadeShaders(gpu.MaterialShaders));
        if (shadeShaders != GpuState.Ready && shaders != GpuState.Failed)
            shaders = shadeShaders;

        if (shaders != GpuState.Loading)
            _built.TrySetResult();

        if (shaders == GpuState.Failed)
        {
            _settling.End();
            frame.Fail(
                RenderFailure.Shader,
                "Reflections are not traced",
                "a shader of the reflections was not built."
            );
        }

        if (shaders != GpuState.Ready)
            return null;

        // Without the indirect light, whose probes light what the rays land on and whose view depth the blend reads,
        // nothing is traced, and the history starts afresh once it is back. The indirect light is lacking for good
        // where it is off, its maker is out or rays see no instance, which ends the settling.
        if (input.Light is not { } light)
        {
            if (!_indirect.IsIndirectLightOn || frame.IsUnavailable<IndirectLight>() || traced.Structure.IsNone)
                _settling.End();

            _reflected[frame.View] = default;

            return null;
        }

        Result<GpuBuffer> constants = _gpu.GetBuffer(
            new("Reflection constants", Unsafe.SizeOf<ReflectionConstants>()),
            frame.View
        );
        (int Width, int Height) tracedSize = gpu.Scaled(_indirect.IndirectScale);

        // A view that lost its history at the same size draws another world: its history starts afresh at that
        // world's first frame.
        if (!gpu.HasHistory && _reflected[frame.View].Traced == tracedSize)
            _reflected[frame.View] = default;

        ((int Width, int Height)? lastTraced, int lastFrames) = _reflected[frame.View];
        int frames = lastTraced == tracedSize ? lastFrames + 1 : 1;
        GIView view = new(
            graph,
            frame.View,
            gpu,
            constants.Value,
            tracedSize,
            GIView.GetWorld(gpu, traced, input.Sky),
            SkyTables.GetSkyType(input.Sky, _openSky)
        );
        Result<TracedReflectionConstants> reflections = Prepare(_gpu, view, frame.Index, lastTraced == tracedSize);
        int written = (int)(frame.Index % 2);
        Result<GpuTexture> depthHistory = _gpu.GetTexture(
            view.GetTraced(GIView.DepthNames[1 - written], GIView.DepthFormat),
            frame.View
        );
        Result<GpuSampler> sampler = _gpu.GetSampler(
            new SamplerDescription { Wrap = TextureWrap.Clamp, MipFilter = TextureFilter.Nearest }
        );
        // PROTO: last frame's lit picture, which a copy at the start of the post effects keeps for the screen trace.
        Result<GpuTexture> picture = _gpu.GetTexture(
            new(
                "Reflected picture",
                gpu.Width,
                gpu.Height,
                GpuFormat.R16G16B16A16Sfloat,
                TextureUsages.Sampled | TextureUsages.CopyDestination
            ),
            frame.View
        );
        Result asked = Result.FindFailure(constants, reflections, depthHistory, sampler, picture);
        if (!asked.Ok)
        {
            _settling.End();
            frame.Fail(RenderFailure.Resource, "Reflections are not traced", asked.Error);

            return null;
        }

        ReflectionConstants reflection = new()
        {
            Reflections = reflections.Value,
            DepthHistory = depthHistory.Value,
            Sampler = sampler.Value,
            TracedWidth = (uint)tracedSize.Width,
            TracedHeight = (uint)tracedSize.Height,
            ReflectionRayRange = Math.Max(_settings.ReflectionRayRange, 0),
            ReflectionHistory = Math.Clamp(
                frames <= SettleFrames
                    ? Math.Min(SettleHistory, _settings.ReflectionHistory)
                    : _settings.ReflectionHistory,
                0,
                1
            ),
            Picture = gpu.HasHistory && lastTraced == tracedSize ? picture.Value : GpuTexture.None,
        };
        // Not staged: nothing is recorded, and the frame's row keeps no reflections, which the shaders read as none.
        if (!graph.Upload(view.Constants, [reflection]))
            return null;

        GpuTexture blended = reflection.Reflections.Blend.Light;
        if (!graph.Upload(gpu.Frame, [blended.Id], _reflectionsOffset))
            return null;

        Record(view, light, reflection);
        graph.Copy(gpu.Hdr, picture.Value, RenderStages.Post);
        _reflected[frame.View] = (tracedSize, frames);
        if (frame.View == 0 && frames >= SettleFrames)
            _settling.End();

        return new Reflections(blended);
    }

    public Task OnDomainLoadingAsync(ImmutableArray<ImmutableArray<IComponent>> entities, CancellationToken cancel) =>
        _built.Task.WaitAsync(cancel);

    /// <summary>
    /// Returns once the first view was traced for <see cref="SettleFrames"/> frames since its history started afresh,
    /// or traces nothing for good: no view, no rays, reflections or indirect light off, a pass that failed, or no
    /// instance that rays see. It waits while the world as rays see it, the indirect light or the shaders are not built
    /// yet.
    /// </summary>
    public Task OnDomainArrivedAsync(CancellationToken cancel) => _settling.WaitAsync(cancel);

    /// <summary>
    /// Asks for the textures at the traced size, the blend's history holding the view as
    /// <paramref name="hasHistory"/> says.
    /// </summary>
    /// <remarks>
    /// The blended light is the indirect specular light that shading reads.
    /// </remarks>
    private static Result<TracedReflectionConstants> Prepare(
        IGraphicsApi gpu,
        in GIView view,
        uint frameIndex,
        bool hasHistory
    )
    {
        RenderGraph graph = view.Graph;
        Result<GpuTexture> traced = gpu.GetTexture(
            view.GetTraced("Traced reflections", GpuFormat.R16G16B16A16Sfloat),
            view.Index
        );
        Result<GpuTexture> rays = gpu.GetTexture(
            view.GetTraced("Reflection rays", GpuFormat.R16G16B16A16Sfloat),
            view.Index
        );
        Result<GpuTexture> resolved = gpu.GetTexture(
            view.GetTraced("Resolved reflections", GpuFormat.R16G16B16A16Sfloat),
            view.Index
        );
        Result<GpuTexture> distance = gpu.GetTexture(
            view.GetTraced("Reflection distance", GpuFormat.R32Sfloat),
            view.Index
        );
        Result<GpuBuffer> hits = gpu.GetBuffer(
            new("Reflection hits", (long)view.TracedSize.Width * view.TracedSize.Height * HitBytes),
            view.Index
        );
        Result<GpuTexture> screen = gpu.GetTexture(
            view.GetTraced("Screen reflections", GpuFormat.R16G16B16A16Sfloat),
            view.Index
        );
        Result<TracedBlendConstants> blend = TracedBlend.Prepare(gpu, view, _blendNames, frameIndex, hasHistory);
        if (Result.FindFailure(traced, rays, resolved, distance, hits, screen, blend) is { Ok: false } failed)
            return Result.Failure(failed.Error);

        return Result.Success(
            new TracedReflectionConstants
            {
                Traced = traced.Value,
                Rays = rays.Value,
                Resolved = resolved.Value,
                Distance = distance.Value,
                Hits = hits.Value,
                Screen = screen.Value,
                Blend = blend.Value,
            }
        );
    }

    /// <summary>
    /// Traces rays from the view's smooth surfaces through the world, then resolves them and blends them with the
    /// history.
    /// </summary>
    /// <remarks>
    /// The lights, the sky and the probes light the rays. The blend reads where the indirect light's diffuse blend kept
    /// last frame's view depth.
    /// </remarks>
    private void Record(in GIView view, in IndirectLight light, in ReflectionConstants reflection)
    {
        RenderGraph graph = view.Graph;
        TracedReflectionConstants reflections = reflection.Reflections;
        (int width, int height) = view.TracedSize;
        GpuConstants header = view.Header;
        ReadOnlySpan<GpuResource> picture = reflection.Picture.IsNone ? [] : [reflection.Picture];
        graph
            .Compute(
                "Trace reflections",
                _traceShader with
                {
                    TypeArguments = view.SkyType,
                },
                header,
                RenderStages.Light
            )
            .Reads(view.World.AsSpan())
            .Reads(view.Gpu.Frame, view.Constants, view.Gpu.Depth, view.Gpu.GBufferNormal, view.Gpu.GBufferSurface)
            .Reads(light.Probes, light.ProbeDepth, view.Gpu.Velocity)
            .Reads(picture)
            .Writes(reflections.Traced, reflections.Rays, reflections.Hits, reflections.Screen)
            .DispatchOver(width, height);

        // Each hit's material evaluated where the ray landed, a pass for each material shader, which shades the hits
        // on the surfaces it draws.
        GpuBuffer hits = reflections.Hits;
        ReadOnlySpan<ShaderEntry> shades = CollectionsMarshal.AsSpan(_shadeShaders);
        for (int shader = 0; shader < shades.Length; shader++)
        {
            graph
                .Compute(
                    "Shade reflection hits",
                    shades[shader],
                    new GpuConstants(header.X, header.Y, (uint)shader),
                    RenderStages.Light
                )
                .Reads(view.World.AsSpan())
                .Reads(view.Gpu.HeapTextures.AsSpan())
                .Reads(view.Gpu.Frame, view.Constants, view.Gpu.Materials, reflections.Traced, reflections.Rays)
                .Writes(hits)
                .DispatchOver(width, height);
        }

        graph
            .Compute(
                "Light reflection hits",
                _lightShader with
                {
                    TypeArguments = view.SkyType,
                },
                header,
                RenderStages.Light
            )
            .Reads(view.World.AsSpan())
            .Reads(view.Gpu.Frame, view.Constants, reflections.Rays, hits, reflections.Screen)
            .Reads(light.Probes, light.ProbeDepth)
            .Writes(reflections.Traced)
            .DispatchOver(width, height);
        graph
            .Compute("Resolve reflections", _resolveShader, header, RenderStages.Light)
            .Reads(
                view.Gpu.Frame,
                view.Constants,
                view.Gpu.Depth,
                view.Gpu.GBufferNormal,
                view.Gpu.GBufferSurface,
                reflections.Traced,
                reflections.Rays
            )
            .Writes(reflections.Resolved, reflections.Distance)
            .DispatchOver(width, height);
        graph
            .Compute("Blend reflections", _blendShader, header, RenderStages.Light)
            .Reads(
                view.Gpu.Frame,
                view.Constants,
                view.Gpu.Depth,
                view.Gpu.GBufferNormal,
                view.Gpu.GBufferSurface,
                view.Gpu.Velocity,
                reflections.Resolved,
                reflections.Distance,
                reflections.Blend.LightHistory,
                reflection.DepthHistory
            )
            .Writes(reflections.Blend.Light)
            .DispatchOver(width, height);
    }

    /// <summary>
    /// Gets the hits' shading specialised on each of <paramref name="materialShaders"/>, by its place.
    /// </summary>
    private ReadOnlySpan<ShaderEntry> GetShadeShaders(IReadOnlyList<ShaderTypeArgument> materialShaders)
    {
        for (int shader = _shadeShaders.Count; shader < materialShaders.Count; shader++)
        {
            _shadeShaders.Add(_shadeShader with { TypeArguments = [materialShaders[shader]] });
        }

        return CollectionsMarshal.AsSpan(_shadeShaders);
    }
}
