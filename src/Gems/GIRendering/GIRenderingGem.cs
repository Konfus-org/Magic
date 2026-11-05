using System.Collections.Immutable;
using System.Numerics;
using Magic;
using Magic.Debugging;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Events;
using Magic.Files;
using Magic.Files.Types;
using Magic.Gpu;
using Magic.Images;
using Magic.Projects;
using Magic.Rendering;
using Magic.Shaders;
using Magic.Threading;
using Magic.Utils;

namespace GIRendering;

/// <summary>
/// Draws the world. <c>Update</c> reads what the frame draws out of the entities; <c>Render</c> records the frame in
/// order, top to bottom, and shows it. It is public because nothing in its assembly makes it: Magic does, by reflection,
/// when a project lists the gem.
/// </summary>
public sealed class GIRenderingGem : IGem, IDisposable
{
    private const int DefaultWidth = 1280;
    private const int DefaultHeight = 720;

    /// <summary>
    /// What a surface with no lit card reflects, as a share of the sun: about what the sky gives a surface.
    /// </summary>
    private const float FallbackLight = 0.15f;

    private readonly IGraphicsApi _gpu;
    private readonly Assets _assets;
    private readonly IEcs _ecs;
    private readonly IFileSystem _files;
    private readonly App _app;
    private readonly EventCoordinator _events;
    private readonly RenderGraph _graph;
    private readonly RenderWorld _world = new();
    private readonly GpuPipelines _pipelines;
    private readonly GeometryPools _pools;
    private readonly TextureTable _textures;
    private readonly MaterialTable _materials;
    private readonly FrameTargets _targets;
    private readonly GlobalIllumination _gi;
    private readonly Translucency _translucency;
    private readonly RayTracing? _rayTracing;
    private readonly Post _post;
    private readonly InstanceTable _instances;
    private readonly CardTable _cards;
    private readonly SunShadows _shadows;
    private readonly Lighting _lighting;
    private readonly Visibility _visibility;
    private readonly FrameView _frameView;
    private readonly CardCapture _capture;
    private RendererPreset _preset = RendererPreset.Quality;
    private DebugView _debugView;
    private bool _isStarting;
    private bool _isReady;
    private bool _hasHzb;
    private bool _hasDrawn;

    /// <summary>
    /// What the passes compiled into, written whole by the compile on its worker and taken by <see cref="Render"/>.
    /// </summary>
    private CompiledPasses? _compiled;

    public GIRenderingGem(
        IGraphicsApi gpu,
        IShaderCompiler shaders,
        Assets assets,
        IMeshPreprocessor meshes,
        IEcs ecs,
        IFileSystem files,
        Threads threads,
        App app,
        EventCoordinator events
    )
    {
        _gpu = gpu;
        _assets = assets;
        _ecs = ecs;
        _files = files;
        _app = app;
        _events = events;
        _graph = new RenderGraph(gpu);
        _pipelines = new GpuPipelines(gpu, shaders, assets);
        _pools = new GeometryPools(gpu, meshes, assets, threads, _pipelines);
        _textures = new TextureTable(gpu, assets, threads);
        _materials = new MaterialTable(gpu, assets, _textures, threads);
        _targets = new FrameTargets(gpu);
        _gi = new GlobalIllumination(gpu, _pipelines);
        _translucency = new Translucency(gpu, _pipelines);
        _rayTracing = gpu.Features.HasRayQuery ? new RayTracing(gpu) : null;
        WantsRayQuery = gpu.Features.HasRayQuery;
        _post = new Post(gpu, _pipelines);
        _instances = new InstanceTable(gpu);
        _cards = new CardTable(gpu);
        _shadows = new SunShadows(gpu, _pipelines);
        _lighting = new Lighting(gpu, _pipelines);
        _visibility = new Visibility(gpu, _pipelines, _pools, _materials);
        _frameView = new FrameView(gpu, _pools, _materials, _visibility, _cards, _rayTracing);
        _capture = new CardCapture(gpu, _pipelines, _cards, _visibility, _frameView);
    }

    /// <summary>
    /// Whether the run asks to trace through the GPU's acceleration structures, which it does by default where it
    /// can; <c>set GIRendering.tracer=field</c> turns it off.
    /// </summary>
    private bool WantsRayQuery { get; set; }

    /// <summary>
    /// Whether the frame traces through the GPU's acceleration structures: asked for, and there is one to trace.
    /// </summary>
    private bool UsesRayQuery => WantsRayQuery && _rayTracing is { Acceleration.IsNone: false };

    public void Dispose()
    {
        _graph.Clear();
        _pipelines.Dispose();
        _targets.Dispose();
        _capture.Dispose();
        _materials.Dispose();
        _textures.Dispose();
        _pools.Dispose();
        _instances.Dispose();
        _cards.Dispose();
        _shadows.Dispose();
        _lighting.Dispose();
        _gi.Dispose();
        _translucency.Dispose();
        _post.Dispose();
        _rayTracing?.Dispose();
        _visibility.Dispose();
        _frameView.Dispose();
    }

    /// <summary>
    /// Reads what the frame draws out of the entities, and starts compiling the passes on the first frame.
    /// </summary>
    public void Update(in Frame frame)
    {
        if (!_isStarting)
        {
            _isStarting = true;
            CompileAsync().FireAndForget();
        }

        WorldCopy.Read(_ecs, _world);
        foreach (SettingChanged setting in frame.Events.Get<SettingChanged>())
        {
            ApplySetting(setting);
        }
    }

    /// <summary>
    /// Draws the frame and submits it.
    /// </summary>
    public void Render(in Frame frame)
    {
        if (!_isReady && Interlocked.Exchange(ref _compiled, null) is { } compiled)
            Start(compiled.Result);

        if (!_isReady)
            return;

        int outputWidth = _app.Window is { Width: > 0, Height: > 0 } window ? window.Width : DefaultWidth;
        int outputHeight = _app.Window is { Width: > 0, Height: > 0 } sized ? sized.Height : DefaultHeight;
        if (!Resize(outputWidth, outputHeight))
            return;

        // What arrived: models into the pools, textures, material rows.
        _materials.Refresh();
        _graph.BeginTransferPass("Upload arrivals");
        _pools.Upload(_graph);
        _textures.Upload(_graph);
        _materials.Upload(_graph);
        _graph.EndPass();

        _capture.Record(_graph);

        // The frame's tables.
        RenderCamera camera = _world.Camera ?? new RenderCamera(new Transform(), 60, 0.05f);
        RenderSun sun = _world.Sun ?? new RenderSun(Vector3.Normalize(new Vector3(0.3f, 0.8f, -0.5f)), Vector3.One, 1);
        GpuBuffer constants = _frameView.Constants;
        int instanceCount = _instances.Fill(
            _world,
            _pools,
            _materials,
            _cards,
            _translucency,
            _assets.GetFailure<Material, Model>()
        );
        _graph.BeginTransferPass("Upload frame");
        _cards.Upload(_graph);
        bool hasArrivals = _pools.HasArrivals;
        _instances.Upload(_graph);
        _rayTracing?.Prepare(_graph, _instances.Rows, _instances.Extras, hasArrivals || _instances.Changes.Length > 0);
        _frameView.Upload(
            _graph,
            constants,
            _frameView.GetMain(camera, sun, frame.Index, _targets, _instances.Buffer, instanceCount, _hasHzb)
        );
        _visibility.ResetCounts(_graph);
        _shadows.Prepare(
            _graph,
            camera.Transform.Position,
            sun.Direction,
            2 * MathF.Tan(camera.FieldOfView * MathF.PI / 360) / _targets.Height,
            _instances.Changes
        );
        _lighting.Prepare(
            _graph,
            _world,
            camera,
            sun,
            _targets,
            _debugView,
            _gi.Constants,
            _translucency.Constants,
            _post.Constants
        );
        _translucency.Prepare(_graph, sun.Direction, camera.Transform.Position, _targets);
        _post.Update(_world.Post, CancellationToken.None);
        _post.Prepare(_graph, _targets, _world.Post, (float)frame.AppSec);
        _gi.Prepare(_graph, _instances.Rows, camera.Transform.Position, frame.Index, sun.Radiance * FallbackLight);
        _pools.Field.Prepare(_graph, constants, camera.Transform.Position, _instances.Changes, instanceCount);
        _instances.ClearChanges();
        _graph.EndPass();

        // The passes, in the order they run.
        _pools.Field.Record(_graph, constants, _instances.Buffer);
        _lighting.RecordTables(_graph, constants, _shadows);
        RenderView view = new(constants, _instances.Buffer, instanceCount, _targets);
        _visibility.RecordMain(_graph, view);
        DrawList translucentDraws = _visibility.GetTranslucentDraws(
            new GpuConstants(constants.Id, _shadows.Constants.Id, _lighting.Constants.Id, _pools.Field.Constants.Id),
            constants,
            _instances.Buffer
        );
        _translucency.RecordShadow(_graph, translucentDraws);
        _shadows.Record(_graph, constants, _instances.Buffer, instanceCount, _pools, _targets);
        _visibility.RecordResolve(_graph, view, "Resolve materials");
        _gi.Record(
            _graph,
            constants,
            _instances.Buffer,
            _visibility.VisibleClusters,
            _shadows,
            _lighting,
            _pools.Field,
            _translucency,
            _cards,
            _targets,
            UsesRayQuery
        );
        _lighting.RecordShading(
            _graph,
            constants,
            _shadows,
            _pools.Field,
            _gi,
            _translucency,
            _cards,
            _instances.Buffer,
            _visibility.VisibleClusters,
            _targets,
            UsesRayQuery
        );
        _translucency.RecordDraws(_graph, translucentDraws, _targets, _shadows, _lighting, _gi);
        _post.Record(_graph, constants, _shadows, _lighting, _pools.Field, _translucency, _targets);

        GpuTexture target = _gpu.WindowTarget;
        if (!target.IsNone)
        {
            _graph.Import(target, GpuAccess.None);
            _graph.BeginTransferPass("Show");
            _graph.Use(_targets.Output, GpuAccess.CopySource);
            _graph.Use(target, GpuAccess.CopyDestination);
            _graph.Recorder.BlitTexture(_targets.Output, target);
            _graph.EndPass();
        }

        // One readback buffer, so one screenshot a frame: the last asked for.
        ReadOnlySpan<ScreenshotRequested> requests = frame.Events.Get<ScreenshotRequested>();
        if (requests.Length > 0)
            Screenshot.RecordReadback(_graph, _targets);

        _gpu.Submit(_graph.Finish());
        _hasHzb = true;
        if (!_hasDrawn)
        {
            _hasDrawn = true;
            _events.Publish(new FirstFrameDrawn());
        }

        if (requests.Length > 0)
        {
            Screenshot
                .SaveAsync(_gpu, _files, _targets.Readback, requests[^1].Path, outputWidth, outputHeight)
                .FireAndForget();
        }
    }

    /// <summary>
    /// Compiles the passes on a worker, for <see cref="Render"/> to make the pipelines from.
    /// </summary>
    private async Task CompileAsync()
    {
        Result<ImmutableArray<CompiledShader>> compiled = await _pipelines
            .CompileAsync(CancellationToken.None)
            .ConfigureAwait(false);
        Interlocked.Exchange(ref _compiled, new CompiledPasses(compiled));
    }

    /// <summary>
    /// Makes the pipelines, the pools and the tables from what the passes compiled into, in the frame, once: a run that
    /// ends before that draws nothing, and finds a shader that does not compile only if it runs long enough to.
    /// </summary>
    private void Start(Result<ImmutableArray<CompiledShader>> compiled)
    {
        if (!compiled.Ok)
        {
            Log.Error($"The renderer draws nothing: {compiled.Error}");

            return;
        }

        Result made = _pipelines.Create(compiled.Value);
        foreach (
            Func<Result> step in (ReadOnlySpan<Func<Result>>)
                [
                    _pools.Create,
                    _materials.Create,
                    _instances.Create,
                    _cards.Create,
                    _shadows.Create,
                    _lighting.Create,
                    _gi.Create,
                    _translucency.Create,
                    _post.Create,
                    () => _rayTracing?.Create() ?? Result.Success(),
                    _pools.Field.Create,
                    _visibility.Create,
                    _frameView.Create,
                    () => _capture.Create(_graph),
                ]
        )
        {
            if (made.Ok)
                made = step();
        }

        if (!made.Ok)
        {
            Log.Error($"The renderer draws nothing: {made.Error}");

            return;
        }

        _isReady = true;
    }

    /// <summary>
    /// Whether the targets fit this frame's size and preset, made again when they don't; false when that failed, and the
    /// frame is not drawn.
    /// </summary>
    private bool Resize(int outputWidth, int outputHeight)
    {
        int width = Math.Max((int)MathF.Round(outputWidth * _preset.Scale), 16);
        int height = Math.Max((int)MathF.Round(outputHeight * _preset.Scale), 16);
        bool fits =
            _targets.Width == width
            && _targets.Height == height
            && _targets.OutputWidth == outputWidth
            && _targets.OutputHeight == outputHeight
            && !_gi.NeedsResize;
        if (fits)
            return true;

        Result resized = _targets.Resize(width, height, outputWidth, outputHeight, _graph);
        if (resized.Ok)
            resized = _gi.Resize(width, height, _graph);

        if (resized.Ok)
            resized = _post.Resize(outputWidth, outputHeight, _graph);

        if (!resized.Ok)
        {
            Log.Error($"The frame is not drawn: {resized.Error}");

            return false;
        }

        _hasHzb = false;

        return true;
    }

    private void ApplySetting(in SettingChanged setting)
    {
        if (setting.Key == "GIRendering.tracer")
        {
            if (setting.Value.Equals("field", StringComparison.OrdinalIgnoreCase))
                WantsRayQuery = false;
            else if (setting.Value.Equals("rayquery", StringComparison.OrdinalIgnoreCase) && _rayTracing is not null)
                WantsRayQuery = true;
            else
                Log.Error($"{setting.Key} takes field or, on a GPU that traces rays, rayquery, not '{setting.Value}'.");

            return;
        }

        if (setting.Key == "GIRendering.preset")
        {
            if (RendererPreset.TryParse(setting.Value, out RendererPreset preset))
            {
                _preset = preset;
                _gi.Configure(preset.SlotsPerFrame, preset.HasReflections, preset.ReflectionStride);
            }
            else
            {
                Log.Error($"{setting.Key} takes {RendererPreset.Names}, not '{setting.Value}'.");
            }

            return;
        }

        if (setting.Key != "GIRendering.debug")
            return;

        if (Enum.TryParse(setting.Value, ignoreCase: true, out DebugView view))
            _debugView = view;
        else
            Log.Error($"{setting.Key} takes {string.Join(", ", Enum.GetNames<DebugView>())}, not '{setting.Value}'.");
    }

    /// <summary>
    /// What the passes compiled into, boxed so it is written whole from the compile's worker.
    /// </summary>
    private sealed class CompiledPasses(Result<ImmutableArray<CompiledShader>> result)
    {
        public Result<ImmutableArray<CompiledShader>> Result { get; } = result;
    }
}
