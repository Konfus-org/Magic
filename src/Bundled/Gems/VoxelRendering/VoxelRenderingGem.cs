using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Drawing;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Magic.Contexts.Assets;
using Magic.Contexts.ECS;
using Magic.Contexts.Frames;
using Magic.Contexts.Gpu;
using Magic.Contexts.Rendering;
using Magic.Contexts.Subscriptions;
using Magic.Contexts.Threading;
using Magic.Contexts.Utils;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.VoxelRendering;

/// <summary>
/// Draws the world GPU-driven, as clusters at the detail each part of it needs, its frame laid out by a pipeline script
/// (<see cref="IRenderPipeline"/>, <see cref="VoxelRenderingSettings.Pipeline"/>), which records the renderer's steps
/// and its own passes in order (<see cref="IRendering"/>), every resource read through the GPU's heap. The CPU keeps
/// the GPU's tables in step with the world, a row for each thing that changed (<see cref="Instances"/>), and its models,
/// textures and materials resident; which clusters are drawn, how finely, and how each pixel is lit, the GPU works out
/// each frame.
/// <para>
/// The world is copied in LateUpdate (<see cref="WorldCopy"/>), handed over in Sync, and drawn in Render from that copy,
/// one frame behind. What a domain draws is taken in before it spawns (<see cref="IPreloader"/>), so it is drawn whole
/// in its first frame, and one that replaces the world is readied, drawn out of sight while the window keeps the
/// loading domain's last image, until it has settled. A frame whose passes are not all compiled yet is not shown: the
/// window keeps the last whole one, or is black before the first. Once every view is shown, the overlay script draws
/// over the window, whether or not a view draws into it.
/// </para>
/// </summary>
internal sealed class VoxelRenderingGem : IGem, IPreloader, IRendering, ICache<BlockTexture>, IDisposable
{
    /// <summary>
    /// The pipeline script run when the settings name none: the renderer's own, whichever gem or project has it.
    /// </summary>
    private const string PipelinePath = "Scripts/DefaultPipeline.cs";

    /// <summary>
    /// The overlay script run when the settings name none: the debug UI's, whichever gem or project has it.
    /// </summary>
    private const string OverlayPath = "Scripts/DebugUi.cs";

    /// <summary>
    /// Frames a domain being readied is drawn at the least: its exposure's first metering, and a frame lit by it.
    /// </summary>
    private const int SettleFrames = 2;

    [SuppressMessage(
        "Style",
        "IDE0032:Use auto property",
        Justification = "Instruments are made from it as the type starts, so it is a field, set first.")]
    private static readonly Meter _meter = new("Magic.Gems.VoxelRendering");

    private readonly IGpuDriver _gpu;
    private readonly VoxelRenderingSettings _settings;
    private readonly Assets _assets;
    private readonly World _world;
    private readonly Threads _threads;
    private readonly IScripts? _scripts;
    private readonly WorldCopy _copy;
    private readonly GpuTables _tables;
    private readonly GpuTextures _textures;
    private readonly GpuModels _models;
    private readonly GpuPipelines _pipelines;
    private readonly GpuMaterials _materials;
    private readonly FailureHandler _failures;
    private readonly Instances _instances;
    private readonly Views _views;
    private readonly Output _output;
    private readonly FramePool _pool;
    private readonly Passes _passes;
    private readonly SkyPasses _sky;
    private readonly WorldPasses _worldPasses;
    private readonly Preloader _preloader;
    private readonly CancellationToken _stopping;
    private readonly List<Subscription> _watching = [];
    private readonly List<RenderView> _shownViews = [];
    private readonly HashSet<GpuTexture> _cleared = [];

    /// <summary>
    /// What each view shows this frame, as the pipeline script said (<see cref="Output"/>).
    /// </summary>
    private readonly List<(RenderView View, GpuTexture Image)> _shown = [];

    /// <summary>
    /// What other threads tell the render thread, taken in as Render starts, in the order told
    /// (<see cref="TakeChanges"/>).
    /// </summary>
    private readonly Channel<Change> _changes = Channel.CreateUnbounded<Change>(
        new UnboundedChannelOptions { SingleReader = true });

    /// <summary>
    /// The pipeline script the frame is recorded by, none until it is made; set on the render thread, read by Sync on
    /// the main thread, while the render thread waits.
    /// </summary>
    private volatile IRenderPipeline? _pipeline;
    private IOverlayScript? _overlay;

    /// <summary>
    /// The scripts last asked for, which a made script is taken only for. Render thread.
    /// </summary>
    private Handle<Script> _pipelineAsked;
    private Handle<Script> _overlayAsked;

    /// <summary>
    /// Whether a whole frame was shown yet, after which an incomplete one leaves the window as it was. Render thread.
    /// </summary>
    private bool _hasShown;

    public VoxelRenderingGem(
        IGpuDriver gpu,
        IShaderCompiler compiler,
        Assets assets,
        Caches caches,
        World world,
        Events events,
        Threads threads,
        App app,
        VoxelRenderingSettings settings,
        IWindow? window = null,
        IDebugUI? debug = null,
        IScripts? scripts = null)
    {
        _gpu = gpu;
        _settings = settings;
        _assets = assets;
        _world = world;
        _threads = threads;
        _scripts = scripts;
        CancellationToken stopping = app.Quitting;
        _stopping = stopping;
        _failures = new FailureHandler(assets, debug);
        _tables = new GpuTables(gpu);
        _textures = new GpuTextures(gpu, assets, caches, settings, stopping);
        _models = new GpuModels(gpu, assets, _tables, _textures, _failures, settings, stopping);
        _pipelines = new GpuPipelines(gpu, compiler, assets, _failures, stopping);
        _materials = new GpuMaterials(assets, _tables, _textures, _pipelines, _failures, stopping);
        _instances = new Instances(_tables, _models, _materials, _failures, assets);
        _copy = new WorldCopy(world);
        _views = new Views(gpu, window, settings);
        _output = new Output(gpu, compiler, stopping);
        _pool = new FramePool(gpu);
        _passes = new Passes(gpu, _pipelines, assets);
        _sky = new SkyPasses(gpu, _passes, assets);
        _worldPasses = new WorldPasses(gpu, _passes, _pipelines, _materials, _output.Raised, assets);
        _preloader = new Preloader(_models, _materials);

        _watching.Add(events.Watch<AssetReloaded<Model>>(reloaded => Tell(new Change(Model: reloaded.Asset))));
        _watching.Add(events.Watch<AssetReloaded<Texture>>(reloaded => Tell(new Change(Texture: reloaded.Asset))));
        _watching.Add(events.Watch<AssetReloaded<Material>>(reloaded => Tell(new Change(Material: reloaded.Asset))));
        _watching.Add(events.Watch<AssetReloaded<Shader>>(reloaded => Tell(new Change(Shader: reloaded.Asset))));
        _watching.Add(events.Watch<AssetReloaded<Script>>(_ => Tell(new Change(IsScriptBuilt: true))));
    }

    // The engine finds a cache's format only on a gem, so these forward to the type it keeps.
    public int Version => BlockTexture.CacheVersion;

    public Frame Time { get; private set; }

    public IReadOnlyList<RenderView> Views => _shownViews;

    public RenderEffects<IPostEffect> PostEffects => _copy.Front.Look?.Effects ?? default;

    public RenderEffects<IWeatherEffect> Weather => _copy.Front.Weather?.Effects ?? default;

    public GpuBuffer Failures => _output.Raised;

    /// <summary>
    /// The gem's meter, which its parts record on (Core's README, Telemetry).
    /// </summary>
    internal static Meter Meter => _meter;

    public void Dispose()
    {
        foreach (Subscription watch in _watching)
        {
            watch.Dispose();
        }

        (_pipeline as IDisposable)?.Dispose();
        (_overlay as IDisposable)?.Dispose();
        _copy.Dispose();
        _worldPasses.Dispose();
        _sky.Dispose();
        _pool.Dispose();
        _output.Dispose();
        _views.Dispose();
        _pipelines.Dispose();
        _models.Dispose();
        _textures.Dispose();
        _tables.Dispose();
    }

    public void Write(BlockTexture value, Stream bytes) => value.Write(bytes);

    public Result<BlockTexture> Read(Stream bytes) => BlockTexture.Read(bytes);

    /// <summary>
    /// The copy is done: what is gone and what outgrew it is taken in, after the systems that copied the world ran in
    /// the ECS's LateUpdate, before this.
    /// </summary>
    public void LateUpdate(in Frame frame) => _copy.Finish();

    /// <summary>
    /// Hands the copy to Render, now that the render thread is done with the one before, and lets the pipeline script
    /// copy what it reads of the world.
    /// </summary>
    public void Sync(in Frame frame)
    {
        _copy.Swap();
        _pipeline?.Sync(_world);
    }

    /// <summary>
    /// Takes in what changed, keeps the world's tables and what they name resident, works out each camera's view, and
    /// records the frame as the pipeline script lays it out; then shows each view's finished image in its target and
    /// draws the overlay over the window. While a domain that replaces the world is readied, the world is drawn as ever
    /// but the window shows the image kept as it loaded, until the world is shown.
    /// </summary>
    public void Render(in Frame frame)
    {
        TakeChanges();
        AskScripts();
        Renderables copy = _copy.Front;
        _preloader.Take(copy.Stream);
        if (!copy.IsLoading) _output.Forget();

        CameraCopy? main = GetMainCamera(copy);
        Vector3? player = copy.Player ?? main?.Transform.Position;
        KeepResident(copy, player ?? default, frame);
        (SurfaceKey sky, int skyRow) = _materials.GetSky(copy.Sky);
        _failures.Say(_pipelines.Failed);
        List<View> views = _views.Prepare(new ViewFrame(copy, frame, player, GetWorldIds(skyRow)));
        _tables.Upload();
        _output.SendBanner(_failures);

        _shownViews.Clear();
        foreach (View view in views)
        {
            _shownViews.Add(view.Render);
        }

        Time = frame;
        _shown.Clear();
        _passes.Sky = sky;
        _pipelines.BeginFrame();
        _worldPasses.BeginFrame(GetCapacity());
        _pipeline?.Record(this);
        bool isComplete = _pipeline is not null && _pipelines.IsFrameComplete;
        _pool.EndFrame();
        _worldPasses.EndFrame();
        _preloader.Answer(
            copy.Stream,
            isComplete,
            copy.IsHolding && copy.Stream.Frame >= SettleFrames && isComplete && !_instances.HasWaiting());

        _cleared.Clear();
        if (isComplete) Show(views, copy, frame);

        // Before the first whole frame the window is black; after it, an incomplete one leaves it as it was.
        if (isComplete || !_hasShown)
        {
            ClearWindow();
            RecordOverlay(frame);
        }

        _hasShown |= isComplete && views.Count > 0;
        _textures.EndFrame();
    }

    /// <summary>
    /// How far what streams into the world is taken in, as the render thread last found it: its models in, its
    /// materials packed with their textures in and every surface's pipelines compiled, and the frame drawn whole; what
    /// failed counts as in, drawn as the failure (<see cref="Preloader"/>).
    /// </summary>
    public float GetProgress(in WorldStream stream) => _preloader.GetProgress(stream);

    /// <summary>
    /// Ready once the world being readied, drawn out of sight while the window shows the image kept as it loaded, has
    /// settled, as the render thread last found it: drawn for its exposure's first metering and a frame lit by it,
    /// whole, every instance's look in.
    /// </summary>
    public bool IsReady(in WorldStream stream) => _preloader.IsReady(stream);

    public GpuTexture Texture(in GpuTextureDescription description) => _pool.Texture(description);

    public GpuBuffer Buffer(uint bytes) => _pool.Buffer(bytes);

    public GpuSampler Sampler(in GpuSamplerDescription description) => _pool.Sampler(description);

    public VisibleWorld Cull(RenderView view, CullPhase phase) => _worldPasses.Cull(view, phase);

    public void DrawVisibility(
        RenderView view,
        in VisibleWorld visible,
        GpuTexture ids,
        GpuTexture depth,
        GpuContents contents) => _worldPasses.DrawVisibility(view, visible, ids, depth, contents);

    public void BuildDepthPyramid(RenderView view, GpuTexture depth) => _worldPasses.BuildDepthPyramid(view, depth);

    public SurfaceTiles Classify(RenderView view, GpuTexture ids) => _worldPasses.Classify(view, ids);

    public void Resolve(RenderView view, in SurfaceTiles tiles, GpuTexture ids, in GBuffer targets)
        => _worldPasses.Resolve(view, tiles, ids, targets);

    public SkyLight CaptureSky(RenderView seenFrom) => _sky.Capture(seenFrom);

    public void DrawSky(RenderView view, GpuTexture depth, GpuTexture target) => _sky.Draw(view, depth, target);

    public void Dispatch<T>(
        Handle<Shader> shader,
        in T constants,
        GpuGroups groups,
        ReadOnlySpan<GpuResource> reads,
        ReadOnlySpan<GpuResource> writes)
        where T : unmanaged => _passes.Dispatch(shader, constants, groups, reads, writes);

    public void Dispatch<T>(
        Handle<Shader> shader,
        in T constants,
        GpuBuffer arguments,
        uint offset,
        ReadOnlySpan<GpuResource> reads,
        ReadOnlySpan<GpuResource> writes)
        where T : unmanaged => _passes.Dispatch(shader, constants, arguments, offset, reads, writes);

    public GpuTexture Effect<T>(RenderView view, Handle<Shader> shader, GpuTexture image, in T values)
        where T : unmanaged
    {
        Size size = _gpu.GetSize(image);
        GpuTexture output = _pool.Texture(new GpuTextureDescription(
            _gpu.GetFormat(image),
            GpuTextureUsage.ComputeWrite | GpuTextureUsage.Sampled,
            (uint)size.Width,
            (uint)size.Height));
        _passes.Dispatch(
            shader,
            new EffectConstants<T>(view.Constants, image, output, 0, values),
            GpuGroups.For(size, 8),
            [image],
            [output]);

        return output;
    }

    public void Output(RenderView view, GpuTexture image) => _shown.Add((view, image));

    public async Task<Result<GpuImage>> CaptureAsync(CancellationToken cancel)
    {
        // The capture is asked for on the render thread, which the GPU is used from, and its image awaited here.
        Task<Result<GpuImage>> capture = await _threads.PostAsync(
            EngineThread.Render,
            () =>
            {
                GpuTexture window = _views.GetWindow();

                return window != GpuTexture.None
                    ? _gpu.CaptureAsync(window)
                    : Task.FromResult(Result.Failure<GpuImage>("There is no window to capture."));
            },
            cancel).ConfigureAwait(false);

        return await capture.WaitAsync(cancel).ConfigureAwait(false);
    }

    public Task<Result<GpuImage>> ReadAsync(GpuTexture texture, CancellationToken cancel)
        => _threads.PostAsync(
            EngineThread.Render,
            () =>
            {
                Size size = _gpu.GetSize(texture);
                Result<byte[]> read = _gpu.Read(texture);

                return read.Ok
                    ? Result.Success(new GpuImage(size.Width, size.Height, read.Value))
                    : Result.Failure<GpuImage>(read.Error);
            },
            cancel);

    public Task<Result<byte[]>> ReadAsync(GpuBuffer buffer, uint bytes, CancellationToken cancel)
        => _threads.PostAsync(EngineThread.Render, () => _gpu.Read(buffer, bytes), cancel);

    /// <summary>
    /// The camera drawing into the window, else the first; none without a camera.
    /// </summary>
    private static CameraCopy? GetMainCamera(Renderables copy)
    {
        foreach (CameraCopy camera in copy.Cameras)
        {
            if (camera.Camera.Target.IsWindow) return camera;
        }

        return copy.Cameras.Count > 0 ? copy.Cameras[0] : null;
    }

    /// <summary>
    /// The instances' rows from this frame's copy of the world, then what nothing holds any more let go and what came
    /// in since the last frame taken in: textures first, as models' cards and materials are worked out from them, and
    /// the pipelines made before the materials, which are drawn once theirs are.
    /// </summary>
    private void KeepResident(Renderables copy, Vector3 player, in Frame frame)
    {
        _instances.Update(copy, (float)frame.RealSec, player, copy.IsHolding);
        _failures.Label(_instances);
        _materials.Purge();
        _models.Purge();
        _textures.Purge();
        _textures.Take();
        _models.Take();
        _pipelines.Take();
        _materials.Take();
    }

    /// <summary>
    /// What the views' constants name the world by this frame: the tables' buffers, which change as they grow, and
    /// the rest.
    /// </summary>
    private WorldIds GetWorldIds(int skyRow) => new(
        _tables.Instances.Buffer.Id,
        _tables.Models.Buffer.Id,
        _tables.Levels.Buffer.Id,
        _tables.Clusters.Buffer.Id,
        _tables.Cards.Buffer.Id,
        _tables.Materials.Buffer.Id,
        _tables.MaterialBins.Buffer.Id,
        _models.Geometry.Id,
        _textures.Sampler.Id,
        _output.Raised.Id,
        (uint)skyRow,
        _sky.Sh.Id);

    /// <summary>
    /// How much of the world each view's culling buffers hold, from the settings and what the world has.
    /// </summary>
    private WorldCapacity GetCapacity()
    {
        uint pieces = (uint)Math.Max(_settings.PieceCapacity, 1024);
        uint instances = (uint)_instances.Capacity;

        // Room for every instance eight times over, so a frame that sees the whole world at once fits.
        uint jobs = Math.Max(Math.Max(pieces / 16, 8 * instances), 1024);

        return new WorldCapacity(instances, pieces, pieces, jobs, (uint)Math.Max(_materials.ResolveBins.Count, 1));
    }

    /// <summary>
    /// Shows each view's finished image in its target, or, while the world is readied out of sight, the image kept as
    /// it loaded in the window; and keeps the window's image while a domain loads in place of the world.
    /// </summary>
    private void Show(List<View> views, Renderables copy, in Frame frame)
    {
        foreach ((RenderView rendered, GpuTexture image) in _shown)
        {
            if (views.Find(view => view.Id == rendered.Id) is not { } view) continue;

            if (copy.IsHolding && view.Target.IsWindow)
            {
                DrawKept(view, frame);

                continue;
            }

            // Kept before it is drawn, so the image is left as a shader reads it.
            if (copy.IsLoading && view.Target.IsWindow) _output.Keep(image);

            Draw(view.Target, image, frame);
        }
    }

    /// <summary>
    /// The window while the world is readied out of sight: the image kept as it loaded, in place of the view, which is
    /// drawn but not shown; black when none was kept.
    /// </summary>
    private void DrawKept(View view, in Frame frame)
    {
        if (_output.Kept == GpuTexture.None) return;

        Draw(view.Target, _output.Kept, frame);
    }

    private void Draw(ViewTarget target, GpuTexture image, in Frame frame)
        => _output.Draw(
            target,
            image,
            !_settings.Upscale,
            _cleared.Add(target.Texture),
            (float)frame.RealSec,
            (uint)(frame.Index % uint.MaxValue));

    /// <summary>
    /// Clears the window to black when no view drew into it this frame, so it never shows what its image held before.
    /// </summary>
    private void ClearWindow()
    {
        GpuTexture window = _views.GetWindow();
        if (window != GpuTexture.None && _cleared.Add(window)) _output.Clear(window);
    }

    /// <summary>
    /// Draws the overlay script over the window, whole, in the colours it shows.
    /// </summary>
    private void RecordOverlay(in Frame frame)
    {
        GpuTexture window = _views.GetWindow();
        Size size = window == GpuTexture.None ? Size.Empty : _gpu.GetSize(window);

        if (_overlay is not { } overlay || size.IsEmpty) return;

        ViewTarget target = new(window, new Rectangle(Point.Empty, size), _gpu.GetFormat(window), true);
        overlay.Record(
            new OverlayContext(_gpu.Commands, window, target.Area, target.Format, target.Encoding, frame));
    }

    /// <summary>
    /// Asks for the scripts the settings name, or the renderer's own, when they are not the ones last asked for: made on
    /// a worker and taken in as a change once made.
    /// </summary>
    private void AskScripts()
    {
        Handle<Script> pipeline = _settings.Pipeline == Handle<Script>.None
            ? _assets.Find<Script>(PipelinePath)
            : _settings.Pipeline;
        if (pipeline != _pipelineAsked)
        {
            _pipelineAsked = pipeline;
            CreateScriptAsync(pipeline, isPipeline: true).FireAndForget();
        }

        Handle<Script> overlay = _settings.Overlay == Handle<Script>.None
            ? _assets.Find<Script>(OverlayPath)
            : _settings.Overlay;
        if (overlay != _overlayAsked)
        {
            _overlayAsked = overlay;
            CreateScriptAsync(overlay, isPipeline: false).FireAndForget();
        }
    }

    /// <summary>
    /// Creates <paramref name="handle"/>'s script with the services it asks for, told back as a change; why not is an
    /// error, and the script it would replace goes on.
    /// </summary>
    private async Task CreateScriptAsync(Handle<Script> handle, bool isPipeline)
    {
        string role = isPipeline ? "pipeline script" : "overlay script";

        if (handle == Handle<Script>.None)
        {
            if (isPipeline) Debugging.Error($"Nothing is drawn: there is no {role}, as no {PipelinePath} is found.");

            return;
        }

        if (_scripts is null)
        {
            Debugging.Error($"Nothing is drawn: the {role} needs a scripting gem, and the project lists none.");

            return;
        }

        Result<Script> asset = await _assets.LoadAsync(handle, _stopping).ConfigureAwait(false);
        if (!asset.Ok)
        {
            Debugging.Error($"The {role} does not load: {asset.Error}");

            return;
        }

        Result<IScript> made = _scripts.Create(asset.Value);
        if (!made.Ok)
        {
            Debugging.Error($"The {role} {asset.Value.Type.Name} cannot be made: {made.Error}");

            return;
        }

        if (isPipeline ? made.Value is not IRenderPipeline : made.Value is not IOverlayScript)
        {
            (made.Value as IDisposable)?.Dispose();
            Debugging.Error($"The {role} {asset.Value.Type.Name} is not one.");

            return;
        }

        Tell(new Change(Script: made.Value, ScriptHandle: handle));
    }

    /// <summary>
    /// Tells the render thread <paramref name="change"/>, from any thread; it is taken in as the next Render starts.
    /// </summary>
    private void Tell(in Change change) => _changes.Writer.TryWrite(change);

    /// <summary>
    /// Takes in what the main thread and workers told the render thread since the last frame, in the order told.
    /// </summary>
    private void TakeChanges()
    {
        while (_changes.Reader.TryRead(out Change change))
        {
            if (change.Model is { } model) _models.Reload(model);

            if (change.Texture is { } texture) _textures.Reload(texture);

            if (change.Material is { } material) _materials.Reload(material);

            if (change.Shader is { } shader)
            {
                _materials.Reload(shader);
                _pipelines.Reload();
            }

            // The scripts were built again: each is made again, the one before drawing until it is.
            if (change.IsScriptBuilt)
            {
                _pipelineAsked = Handle<Script>.None;
                _overlayAsked = Handle<Script>.None;
            }

            if (change.Script is { } script) TakeScript(script, change.ScriptHandle);
        }
    }

    /// <summary>
    /// Takes a script made in place of the one it replaces, which is let go, if it is still the one asked for.
    /// </summary>
    private void TakeScript(IScript script, Handle<Script> handle)
    {
        if (script is IRenderPipeline pipeline && handle == _pipelineAsked)
        {
            (_pipeline as IDisposable)?.Dispose();
            _pipeline = pipeline;

            return;
        }

        if (script is IOverlayScript overlay && handle == _overlayAsked)
        {
            (_overlay as IDisposable)?.Dispose();
            _overlay = overlay;

            return;
        }

        (script as IDisposable)?.Dispose();
    }

    /// <summary>
    /// A post effect's constants, as <see cref="IRendering.Effect"/> says: the view's constants, the image and the
    /// texture written, a word of padding, then the effect's values.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct EffectConstants<T>(
        GpuBuffer View,
        GpuTexture Image,
        GpuTexture Output,
        uint Padding,
        T Values)
        where T : unmanaged;

    /// <summary>
    /// One change told to the render thread: whichever of these it carries. An asset made again, to take in in place
    /// of what it had; that the scripts were built again; or a script made, for the script asset it was made from.
    /// </summary>
    private readonly record struct Change(
        Model? Model = null,
        Texture? Texture = null,
        Material? Material = null,
        Shader? Shader = null,
        bool IsScriptBuilt = false,
        IScript? Script = null,
        Handle<Script> ScriptHandle = default);
}
