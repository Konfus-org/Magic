using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.IO;
using System.Numerics;
using System.Threading.Channels;
using Magic.Contexts.Assets;
using Magic.Contexts.Frames;
using Magic.Contexts.Gpu;
using Magic.Contexts.Subscriptions;
using Magic.Contexts.Utils;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.ClusterRendering;

/// <summary>
/// Draws the world GPU-driven, as clusters at the detail each part of it needs: each camera's view of the world's
/// triangles into a visibility buffer, then each pixel shaded once into the G-buffer, lit once a pixel, then the sky,
/// transparent surfaces lit as they are drawn, and the world's effects. What runs is data: the frame pipeline the
/// settings pick (<see cref="ClusterRenderingSettings.Pipeline"/>) and the effects the world's <c>PostProcessing</c>
/// names, their passes run in their stages (<see cref="PassStage"/>). The CPU only keeps the GPU's tables in step with
/// the world, a row for each thing that changed (<see cref="WorldProcessor"/>, <see cref="GpuResources"/>), and records
/// a fixed list of passes; which clusters are drawn, how finely, which lights reach which pixels, all of it the GPU
/// works out each frame.
/// <para>
/// The world is copied in LateUpdate (<see cref="WorldProcessor"/>), handed over in Sync, and drawn in Render from that
/// copy, one frame behind. What a domain draws is taken in before it spawns (<see cref="IPreloader"/>), so it is drawn
/// whole in its first frame, and one that replaces the world is readied, drawn out of sight while the window keeps the
/// loading domain's last image, until its light, its shadows and its exposure have settled, so it is shown as it should
/// be from its first frame shown. While a debug view is named (<see cref="DebugViewChanged"/>), its pass
/// (<c>Passes/Debug/Name.pass</c>) runs after the world's effects and what it draws is shown in place of the finished
/// image, or the failure's magenta saying nothing draws while it is not ready; while the debug UI shows, each instance
/// drawn as the failure says why where it is. Once every view is shown, the overlay stage runs once for each target,
/// the window whether or not a view draws into it, with the overlay pipeline's passes, the debug UI's among them.
/// </para>
/// </summary>
internal sealed class ClusterRenderingGem : IGem, IPreloader, ICache<ModelField>, ICache<CompressedTexture>, IDisposable
{
    /// <summary>
    /// The overlay pipeline run when the settings name none: whichever gem or project has it.
    /// </summary>
    private const string OverlayPath = "Pipelines/Overlay.pipeline";

    /// <summary>
    /// The pipeline a frame runs when the settings name none: the renderer's whole frame, whichever gem or project has
    /// it.
    /// </summary>
    private const string PipelinePath = "Pipelines/Clusters.pipeline";

    /// <summary>
    /// The fog's falloff when the weather names none: a ground fog, whichever gem or project has it.
    /// </summary>
    private const string FogFalloffPath = "Curves/HeightFog.curve";

    [SuppressMessage(
        "Style",
        "IDE0032:Use auto property",
        Justification = "The instruments below are made from it as the type starts, so it is a field, set first: an "
            + "auto property goes after the fields and would be read before it is set.")]
    private static readonly Meter _meter = new("Magic.Gems.ClusterRendering");

    private static readonly Counter<int> _movedInstances = _meter.CreateCounter<int>(
        "magic.gems.cluster_rendering.instances.moved",
        "{instance}",
        "Instances that moved, whose shadows and light are drawn again.");
    private static readonly Counter<long> _giRays = _meter.CreateCounter<long>(
        "magic.gems.cluster_rendering.gi.rays",
        "{ray}",
        "Rays the global illumination probes traced.");

    private readonly IGpu _gpu;
    private readonly ClusterRenderingSettings _settings;
    private readonly Assets _assets;
    private readonly WorldProcessor _world;
    private readonly GpuResources _resources;
    private readonly Shadows _shadows;
    private readonly PassGraph _graph;
    private readonly Views _views;
    private readonly Output _output;
    private readonly FailureHandler _failures;
    private readonly Preloader _preloader;
    private readonly CancellationToken _stopping;

    /// <summary>
    /// A target's names while its overlay stage records, the target itself among them; one scope, each target in turn.
    /// </summary>
    private readonly ResourceScope _target;

    /// <summary>
    /// This frame's spheres where something moved, about the main camera, by which shadows and the field are made
    /// again: the frame's <c>Moved</c>.
    /// </summary>
    private readonly GpuTable<Vector4> _moved;
    private readonly Dictionary<GpuTexture, ViewTarget> _targets = [];
    private readonly GiVolume _gi;
    private readonly List<Subscription> _watching = [];
    private readonly List<SurfaceKey> _raster = [];
    private readonly HashSet<GpuTexture> _cleared = [];

    /// <summary>
    /// What other threads tell the render thread, taken in as Render starts, in the order told
    /// (<see cref="TakeChanges"/>): an asset made again, or the debug view named.
    /// </summary>
    private readonly Channel<Change> _changes = Channel.CreateUnbounded<Change>(
        new UnboundedChannelOptions { SingleReader = true });

    /// <summary>
    /// The debug view shown in place of the finished image, as last told (<see cref="DebugViewChanged"/>); empty for
    /// none. Render thread.
    /// </summary>
    private string _debugView = string.Empty;

    /// <summary>
    /// The debug view named that has no pass, said once.
    /// </summary>
    private string _missingView = string.Empty;

    /// <summary>
    /// The weather's fog falloff last asked for, and the curve once it is in (<see cref="GetFogFalloff"/>). Render
    /// thread.
    /// </summary>
    private Handle<Curve> _fogFalloffAsked;
    private Curve? _fogFalloff;

    /// <summary>
    /// Whether the last frame's copy of the world was loading, so the frame the loading domain takes the world's place
    /// is told from the ones after it. Render thread.
    /// </summary>
    private bool _wasLoading;

    /// <summary>
    /// A new world took the old one's place since global illumination was last planned, so its next plan forgets the
    /// old world's light (<see cref="GiVolume.Plan"/>). Render thread.
    /// </summary>
    private bool _isForgetting;

    public ClusterRenderingGem(
        IGpu gpu,
        IShaderCompiler compiler,
        Assets assets,
        Caches caches,
        World world,
        Events events,
        Threads threads,
        Lifetime lifetime,
        ClusterRenderingSettings settings,
        IWindow? window = null,
        IDebugUI? debug = null,
        IScripts? scripts = null)
    {
        _gpu = gpu;
        _settings = settings;
        _assets = assets;
        CancellationToken stopping = lifetime.Stopping;
        _stopping = stopping;
        _failures = new FailureHandler(assets, debug);
        _graph = new PassGraph(gpu, compiler, assets, scripts, threads, _failures, stopping);
        _resources = new GpuResources(gpu, assets, caches, _graph, _failures, settings, stopping);
        _world = new WorldProcessor(world, _resources, _failures, assets);
        _shadows = new Shadows(_resources);
        _views = new Views(gpu, window, _resources, _world.Instances, settings);
        _output = new Output(gpu, _resources, compiler, stopping);
        _preloader = new Preloader(_resources, _graph);
        _target = _resources.MakeScope();
        _moved = _resources.MakeTable<Vector4>(GpuContract.Moved, 256);
        _gi = new GiVolume(_resources);

        _watching.Add(events.Watch<AssetReloaded<Model>>(reloaded => Tell(new Change(Model: reloaded.Asset))));
        _watching.Add(events.Watch<AssetReloaded<Texture>>(reloaded => Tell(new Change(Texture: reloaded.Asset))));
        _watching.Add(events.Watch<AssetReloaded<Material>>(reloaded => Tell(new Change(Material: reloaded.Asset))));
        _watching.Add(
            events.Watch<AssetReloaded<Shader>>(
                reloaded => Tell(new Change(Shader: reloaded.Asset, Invalidates: true))));
        _watching.Add(events.Watch<AssetReloaded<Pipeline>>(_ => Tell(new Change(Invalidates: true))));
        _watching.Add(
            events.Watch<AssetReloaded<Curve>>(reloaded => Tell(new Change(Curve: reloaded.Asset, Invalidates: true))));
        _watching.Add(events.Watch<AssetReloaded<Script>>(_ => Tell(new Change(Invalidates: true))));
        _watching.Add(events.Watch<DebugViewChanged>(changed => Tell(new Change(DebugView: changed.Name))));
    }

    // The engine finds a cache's format only on a gem, so these forward to the types they keep.
    public int Version => ModelField.CacheVersion;

    int ICache<CompressedTexture>.Version => CompressedTexture.CacheVersion;

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

        _world.Dispose();
        _output.Dispose();
        _views.Dispose();
        _graph.Dispose();
        _resources.Dispose();
    }

    public void Write(ModelField value, Stream bytes) => value.Write(bytes);

    public Result<ModelField> Read(Stream bytes) => ModelField.Read(bytes);

    void ICache<CompressedTexture>.Write(CompressedTexture value, Stream bytes) => value.Write(bytes);

    Result<CompressedTexture> ICache<CompressedTexture>.Read(Stream bytes) => CompressedTexture.Read(bytes);

    /// <summary>
    /// The copy is done: what is gone and what outgrew it is taken in, after the systems that copied the world ran in
    /// the ECS's LateUpdate, before this.
    /// </summary>
    public void LateUpdate(in Frame frame) => _world.Finish();

    /// <summary>
    /// Hands the copy to Render, now that the render thread is done with the one before.
    /// </summary>
    public void Sync(in Frame frame) => _world.Swap();

    /// <summary>
    /// Takes in what changed, places the key light's shadow about the main camera, then for every camera with somewhere
    /// to draw records the frame's stages once (probes), each view's stages, and the view shown in its target: up to
    /// the stage the debug view names, and what that stage made shown in place of the finished image. A window nothing
    /// is drawn into, with no camera on it or while the frame's passes first compile, is cleared to black. While a
    /// domain that replaces the world is readied, the world is drawn as ever but the window shows the image kept as it
    /// loaded, until the world is shown.
    /// </summary>
    public void Render(in Frame frame)
    {
        TakeChanges();
        _preloader.Take();
        Renderables copy = _world.Front;
        if (!copy.IsLoading)
        {
            _preloader.Show();
            _output.Forget();
        }

        // A new world: the loading domain taking the world's place, or the domain replacing it readied from this frame.
        _isForgetting |= (copy.IsLoading && !_wasLoading) || _preloader.Settling == 0;
        _wasLoading = copy.IsLoading;
        CameraCopy? main = GetMainCamera(copy);
        Vector3? player = copy.Player ?? main?.Transform.Position;
        UpdateRows(copy, main, player, frame);
        (SurfaceKey skySurface, int skyRow) = _resources.GetSky(copy.Sky);
        UpdateGraph(copy, skySurface);
        PlanGi(main, frame);
        Curve? fogFalloff = GetFogFalloff(copy.Weather?.FogFalloff ?? Handle<Curve>.None);
        Settle(copy, fogFalloff);
        List<View> views = _views.Prepare(
            new ViewFrame(copy, frame, skyRow, MakeCounts(), player, HasJitter(), fogFalloff));
        View? shown = views.Find(view => view.Target.IsWindow) ?? (views.Count > 0 ? views[0] : null);
        if (shown is not null) WriteMoved(main?.Transform.Position ?? shown.Position);

        Listing? listing = _graph.Current;
        if (listing is not null) _failures.Say(listing.Failures);

        _resources.Upload();
        _cleared.Clear();
        if (listing is null)
        {
            ClearWindow();
            _resources.EndFrame();

            return;
        }

        if (shown is null)
        {
            ClearWindow();
            RecordOverlays(listing, views, null, frame);
            _resources.EndFrame();

            return;
        }

        _output.SendBanner(_failures);
        RecordScope frameScope = new(
            _resources,
            null,
            shown.Constants,
            shown.Counts,
            [],
            GpuBuffer.None,
            skySurface,
            frame);
        for (PassStage stage = PassStage.Probes; stage < PassStage.Cull; stage++)
        {
            _graph.Record(listing, stage, frameScope);
        }

        foreach (View view in views)
        {
            RenderView(view, listing, skySurface, frame);
        }

        if (_preloader.IsHolding) DrawKept(shown);
        else if (copy.IsLoading && shown.Target.IsWindow) _output.Keep(GetFinished(shown));

        _resources.Frame.ReleaseUnlisted(listing);
        ClearWindow();
        RecordOverlays(listing, views, shown, frame);
        _resources.EndFrame();
    }

    /// <summary>
    /// Readies the domain taken in, spawned in place of the world: drawn out of sight, the window showing the image kept
    /// as the world loaded, until all that builds up over frames has settled (<see cref="Settle"/>), the window showing
    /// it once the world is shown.
    /// </summary>
    public Task ReadyAsync(CancellationToken cancel) => _preloader.ReadyAsync(cancel);

    /// <summary>
    /// Takes in what <paramref name="prefabs"/> draw before they spawn, so they are drawn whole in their first frame:
    /// their models into the pool, their materials packed with their textures in and every surface's pipelines
    /// compiled, and the frame built for the effects they name. Done once all of it is in, or failed, and so drawn as
    /// the failure; what it hands back keeps it in the pool until it is disposed.
    /// </summary>
    public Task<Subscription> PreloadAsync(
        IReadOnlyList<Prefab> prefabs,
        IProgress<float> progress,
        CancellationToken cancel) => _preloader.PreloadAsync(prefabs, progress, cancel);

    /// <summary>
    /// The view's finished image: what the post stage made, else the lit scene as it is; none until either is made.
    /// </summary>
    private static BoundResource GetFinished(View view)
    {
        if (view.Resources.TryGet("Ldr", out BoundResource ldr)) return ldr;

        return view.Resources.TryGet(GpuContract.Hdr.Name, out BoundResource hdr) ? hdr : default;
    }

    /// <summary>
    /// What <paramref name="view"/> shows while a debug view is named: the texture its pass drew this frame, null while
    /// the pass is not ready or does not run, so what is shown says that nothing draws, never an image from an earlier
    /// frame.
    /// </summary>
    private static BoundResource? DebugImage(View view)
    {
        return view.Resources.TryGet("DebugView", out BoundResource drawn) && drawn.Texture != GpuTexture.None
            ? drawn
            : null;
    }

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
    /// The instances' and lights' rows and the shadow plan from this frame's copy of the world, what nothing holds any
    /// more let go, then what came in since the last frame, so the materials the frame draws with are the ones in.
    /// </summary>
    private void UpdateRows(Renderables copy, CameraCopy? main, Vector3? player, in Frame frame)
    {
        _world.UpdateRows((float)frame.Seconds, player ?? default, _preloader.IsHolding);
        _movedInstances.Add(_world.Instances.Moved.Count);
        if (main is { } camera)
        {
            _shadows.Plan(
                camera.Transform.Position,
                copy.KeyLightDirection,
                _world.Lights.ChangedWords,
                _world.Instances.Moved.Count,
                frame.Index,
                _settings);
        }

        _failures.Label(_world.Instances);
        _resources.Update();
    }

    /// <summary>
    /// The pass graph for this frame's pipelines and the surface variants the instances draw, the debug view's pass
    /// appended, and the preloads checked against what came in.
    /// </summary>
    private void UpdateGraph(Renderables copy, SurfaceKey skySurface)
    {
        _raster.Clear();
        _raster.AddRange(GpuContract.FixedRasterKeys);
        _raster.AddRange(_resources.RasterBins);
        _graph.Append(DebugViewPass(_debugView));
        _graph.Update(
            GetPipeline(),
            _preloader.GetEffects(copy.Effects),
            GetOverlay(),
            new SurfaceVariants(_raster, _resources.ResolveBins, _resources.TransparentSurfaces, skySurface));
        _preloader.Check(_preloader.GetEffects(copy.Effects));
    }

    /// <summary>
    /// The global illumination's plan around the main camera when the pipeline traces it, forgetting the old world's
    /// light the first time after a new world took its place; cleared otherwise, so a pipeline without it carries no
    /// stale field.
    /// </summary>
    private void PlanGi(CameraCopy? main, in Frame frame)
    {
        if (_graph.Current?.HasPass("GiField") == true && main is { } eye)
        {
            _gi.Plan(
                eye.Transform.Position,
                _world.Instances.Moved,
                _world.Instances.Recoloured,
                frame.DeltaSeconds,
                _preloader.Settling,
                _isForgetting,
                _settings);
            _isForgetting = false;
            _giRays.Add(_gi.Rays);

            return;
        }

        _gi.Clear();
    }

    /// <summary>
    /// How much of everything the frame's buffers hold, from the settings and what the world has.
    /// </summary>
    private Counts MakeCounts() => new(
        Instances: (uint)_world.Instances.Capacity,
        Pieces: (uint)Math.Max(_settings.PieceCapacity, 1024),
        Clusters: (uint)Math.Max(_settings.PieceCapacity, 1024),
        // Room for every instance eight times over, so a frame drawing several of the shadow pages that hold the whole
        // world, the clipmap's coarsest levels' after a turn, fits rather than drawing nothing and waiting.
        Jobs: (uint)Math.Max(Math.Max(_settings.PieceCapacity / 16, 8 * _world.Instances.Capacity), 1024),
        RasterBins: GpuContract.RasterBinRows,
        ResolveBins: (uint)Math.Max(_resources.ResolveBins.Count, 1),
        Tiles: 0,
        HizTexels: 0,
        HizLevels: 0,
        Lights: BitOperations.RoundUpToPowerOf2((uint)Math.Clamp(_settings.LightCapacity, 32, 1 << 20)),
        ZBins: Views.ZBinCount,
        LightTileWords: 0,
        GiRays: _gi.Rays,
        GiProbeThreads: _gi.ProbeThreads,
        WorldLights: (uint)_world.Lights.Capacity,
        ClipmapEntries: (uint)_shadows.ClipmapEntries,
        LightEntries: Shadows.LightTableEntries,
        ShadowViews: (uint)Math.Max(_settings.ShadowUpdates, 1),
        ShadowGroups: (uint)(Math.Max(_settings.ShadowUpdates, 1) + Shadows.GetLevelCount(_settings)),
        ShadowCandidates: (uint)(_shadows.WholePages + _shadows.SmallPages));

    /// <summary>
    /// One view's passes, from culling to its UI, then its finished image, or the debug view's, drawn to its target;
    /// nothing drawn, and the target cleared, when the frame has no image for it.
    /// </summary>
    private void RenderView(View view, Listing listing, SurfaceKey skySurface, in Frame frame)
    {
        view.Resources.ReleaseUnlisted(listing);
        RecordScope scope = new(
            _resources,
            view.Resources,
            view.Constants,
            view.Counts,
            view.TransparentRuns,
            view.TransparentPieces,
            skySurface,
            frame);
        for (PassStage stage = PassStage.Cull; stage <= PassStage.Ui; stage++)
        {
            _graph.Record(listing, stage, scope);
        }

        // While the world is readied out of sight, the window shows the image kept in its place (DrawKept).
        if (!_preloader.IsHolding || !view.Target.IsWindow)
        {
            BoundResource? image = _debugView.Length > 0 ? DebugImage(view) : GetFinished(view);
            bool clears = _cleared.Add(view.Target.Texture);
            GpuFilter filter = _settings.Upscale ? GpuFilter.Linear : GpuFilter.Nearest;
            _output.Draw(view.Target, view.Constants, image, filter, clears);
        }

        view.Resources.Swap();
    }

    /// <summary>
    /// The window while the world is readied out of sight: the image kept as it loaded, in <paramref name="shown"/>'s
    /// place, as the view shown in the window is drawn but not shown; black when none was kept, as nothing was shown.
    /// </summary>
    private void DrawKept(View shown)
    {
        if (_output.Kept.Texture == GpuTexture.None || !shown.Target.IsWindow) return;

        _output.Draw(shown.Target, shown.Constants, _output.Kept, GpuFilter.Linear, _cleared.Add(shown.Target.Texture));
    }

    /// <summary>
    /// Counts the frame of a domain being readied, which is ready once what builds up over frames has settled: drawn
    /// for as many frames as the slowest of it takes from nothing (<see cref="GetSettleFrames"/>), the frame built for
    /// the world's effects, every instance's look in and <paramref name="fogFalloff"/>, the fog's, in when the weather
    /// asks for one (<see cref="Preloader.Settle"/>). Whether or not anything is drawn, so a run with no window loads.
    /// </summary>
    private void Settle(Renderables copy, Curve? fogFalloff)
    {
        _preloader.Settle(_preloader.Settling >= GetSettleFrames()
            && _graph.IsBuiltFor(_preloader.GetEffects(copy.Effects))
            && !_world.Instances.HasWaiting()
            && (fogFalloff is not null || _fogFalloffAsked == Handle<Curve>.None));
    }

    /// <summary>
    /// Frames a domain being readied is drawn at the least: what the slowest of what builds up over frames takes from
    /// nothing, global illumination's settling tracings, two turns of the upscale's jitter, or twice the frames the
    /// shadows' pool takes to draw whole at the pages a frame may draw, as a frame whose casters overflow draws half.
    /// </summary>
    private int GetSettleFrames()
    {
        int pages = Math.Max(_settings.ShadowUpdates, 1);
        int shadows = 2 * ((_shadows.WholePages + pages - 1) / pages);

        return Math.Max(GiVolume.SettleTracings, Math.Max(2 * View.JitterPhases, shadows));
    }

    /// <summary>
    /// Records the overlay stage once for each target drawn into this frame, the window among them whether or not a
    /// view draws into it: each is <c>Target</c>, with its own size, format and encoding in the constants, over the
    /// view shown in it, or over black. The constants are <paramref name="shown"/>'s, the view in the window, else the
    /// frame's alone.
    /// </summary>
    private void RecordOverlays(Listing listing, List<View> views, View? shown, in Frame frame)
    {
        _target.ReleaseUnlisted(listing);
        _targets.Clear();
        GpuTexture window = _views.GetWindow();
        System.Drawing.Size size = window == GpuTexture.None ? System.Drawing.Size.Empty : _gpu.GetSize(window);
        if (!size.IsEmpty)
        {
            _targets[window] = new ViewTarget(
                window,
                new System.Drawing.Rectangle(System.Drawing.Point.Empty, size),
                _gpu.GetFormat(window),
                true);
        }

        foreach (View view in views)
        {
            if (!view.Target.IsWindow) _targets.TryAdd(view.Target.Texture, view.Target);
        }

        foreach (ViewTarget target in _targets.Values)
        {
            _target.Set(
                GpuContract.Target,
                BoundResource.Of(target.Texture, (uint)target.Area.Width, (uint)target.Area.Height));
            FrameConstants constants = shown?.Constants ?? default;
            constants.ViewSize = new Vector2(target.Area.Width, target.Area.Height);
            constants.ViewTexel = new Vector2(1f / target.Area.Width, 1f / target.Area.Height);
            constants.OutputSize = constants.ViewSize;
            constants.TargetEncoding = (uint)target.Encoding;
            constants.Time = (float)frame.Seconds;
            constants.FrameIndex = (uint)(frame.Index % uint.MaxValue);
            RecordScope scope = new(
                _resources,
                _target,
                constants,
                shown?.Counts ?? default,
                [],
                GpuBuffer.None,
                default,
                frame,
                target.Format,
                target.Encoding);
            _graph.Record(listing, PassStage.Overlay, scope);
        }
    }

    /// <summary>
    /// The overlay pipeline the settings name, else the one at <see cref="OverlayPath"/>, else none; changed while
    /// running, the next listing built runs it.
    /// </summary>
    private Handle<Pipeline> GetOverlay()
        => _settings.Overlay == Handle<Pipeline>.None ? _assets.Find<Pipeline>(OverlayPath) : _settings.Overlay;

    /// <summary>
    /// The pipeline the settings name, else the one at <see cref="PipelinePath"/>, else none; changed while running,
    /// the next listing built runs it.
    /// </summary>
    private Handle<Pipeline> GetPipeline()
        => _settings.Pipeline == Handle<Pipeline>.None ? _assets.Find<Pipeline>(PipelinePath) : _settings.Pipeline;

    /// <summary>
    /// Clears the window to black when no view drew into it this frame, so it never shows what its image held before.
    /// </summary>
    private void ClearWindow()
    {
        GpuTexture window = _views.GetWindow();
        if (window != GpuTexture.None && _cleared.Add(window)) _output.Clear(window);
    }

    /// <summary>
    /// This frame's spheres where something moved, about <paramref name="origin"/>, the main camera, which the shadows
    /// are placed about.
    /// </summary>
    private void WriteMoved(Vector3 origin)
    {
        for (int i = 0; i < _world.Instances.Moved.Count; i++)
        {
            MovedSphere sphere = _world.Instances.Moved[i];
            _moved.Set(i, new Vector4(sphere.Centre - origin, sphere.Radius));
        }
    }

    /// <summary>
    /// Whether views are drawn a sub-pixel off each frame, which the temporal upscale resolves into detail finer than a
    /// pixel: only while the frame runs it, as nothing else would undo the shake, and not while a debug view is shown,
    /// which is drawn from the view as it is and would shake with it.
    /// </summary>
    private bool HasJitter() => _graph.Current?.HasPass("Taa") == true && _debugView.Length == 0;

    /// <summary>
    /// The pass of the debug view <paramref name="name"/> names, <c>Passes/Debug/Name.pass</c>; none for no view, or
    /// for a name no pass has, which is said once.
    /// </summary>
    private Handle<Pass> DebugViewPass(string name)
    {
        if (name.Length == 0) return Handle<Pass>.None;

        Handle<Pass> pass = _assets.Find<Pass>($"Passes/Debug/{name}.pass");
        if (pass == Handle<Pass>.None && !string.Equals(name, _missingView, StringComparison.Ordinal))
        {
            _missingView = name;
            Debugging.Warn(
                $"There is no debug view {name}: no pass is Passes/Debug/{name}.pass, so the frame is shown as it is.");
        }

        return pass;
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
            if (change.Model is { } model) _resources.Reload(model);

            if (change.Texture is { } texture) _resources.Reload(texture);

            if (change.Material is { } material) _resources.Reload(material);

            if (change.Shader is { } shader) _resources.Reload(shader);

            if (change.Curve is { } curve && curve.Handle == _fogFalloffAsked) _fogFalloff = curve;

            if (change.Invalidates) _graph.Invalidate();

            if (change.DebugView is { } view) _debugView = view;
        }
    }

    /// <summary>
    /// The weather's fog falloff, the one at <see cref="FogFalloffPath"/> when it names none; none while it loads, or
    /// when there is none to load: asked for again when the weather names another, its load told back as a change
    /// (<see cref="TakeChanges"/>), as is its file made again.
    /// </summary>
    private Curve? GetFogFalloff(Handle<Curve> named)
    {
        Handle<Curve> asked = named == Handle<Curve>.None ? _assets.Find<Curve>(FogFalloffPath) : named;

        if (asked == _fogFalloffAsked) return _fogFalloff;

        _fogFalloffAsked = asked;
        _fogFalloff = null;
        if (asked != Handle<Curve>.None) LoadFogFalloffAsync(asked).FireAndForget();

        return null;
    }

    private async Task LoadFogFalloffAsync(Handle<Curve> asked)
    {
        Result<Curve> loaded = await _assets.LoadAsync(asked, _stopping).ConfigureAwait(false);
        if (loaded.Ok) Tell(new Change(Curve: loaded.Value));
        else Debugging.Warn($"The weather's fog falloff cannot be loaded, so it has no fog: {loaded.Error}");
    }

    /// <summary>
    /// One change told to the render thread: whichever of these it carries. An asset made again, to take in in place
    /// of what it had, or a curve in; that the frame's listing is to be built again; or the debug view named.
    /// </summary>
    private readonly record struct Change(
        Model? Model = null,
        Texture? Texture = null,
        Material? Material = null,
        Shader? Shader = null,
        Curve? Curve = null,
        bool Invalidates = false,
        string? DebugView = null);
}
