using System.Diagnostics.Metrics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Magic.Contexts.Assets;
using Magic.Contexts.Frames;
using Magic.Contexts.Gpu;
using Magic.Contexts.Subscriptions;
using Magic.Contexts.Threading;
using Magic.Contexts.Utils;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.ClusterRendering;

/// <summary>
/// Draws the world GPU-driven, as clusters at the detail each part of it needs: each camera's view of the scene's
/// triangles into a visibility buffer, then each pixel shaded once into the G-buffer, lit once a pixel, then the sky,
/// transparent surfaces lit as they are drawn, and the world's effects. What runs is data: the frame pipeline the
/// settings pick (<see cref="ClusterRenderingSettings.Pipeline"/>) and the effects the world's <c>PostProcessing</c>
/// names, their passes run in their stages (<see cref="PassStage"/>). The CPU only keeps the GPU's tables in step with
/// the world, a row for each thing that changed (<see cref="Scene"/>, <see cref="ClusterPool"/>,
/// <see cref="Materials"/>), and records a fixed list of passes; which clusters are drawn, how finely, which lights
/// reach which pixels, all of it the GPU works out each frame.
/// <para>
/// The world is copied in LateUpdate (<see cref="Snapshot"/>), handed over in Sync, and drawn in Render from that copy, one
/// frame behind. What a domain draws is taken in before it spawns (<see cref="IPreloader"/>), so it is drawn whole in
/// its first frame.
/// While a debug view is named (<see cref="DebugViewChanged"/>), its pass (<c>Passes/Debug/Name.pass</c>) runs after the world's
/// effects and what it draws is shown in place of the finished image, or the failure's magenta saying nothing draws while
/// it is not ready; while the debug UI shows, each instance drawn as the failure says why where it is. Once every view
/// is shown, the overlay stage runs once for each target, the window whether or not a view draws into it, with the
/// overlay pipeline's passes, the debug UI's among them.
/// </para>
/// </summary>
internal sealed class ClusterRenderingGem : IGem, IPreloader, ICache<ModelField>, ICache<CompressedTexture>, IDisposable
{
    /// <summary>
    /// The gem's meter, which its parts record on (Core's README, Telemetry).
    /// </summary>
    internal static Meter Meter { get; } = new("Magic.Gems.ClusterRendering");

    private static readonly Counter<int> _movedInstances = Meter.CreateCounter<int>("magic.gems.cluster_rendering.scene.moved", "{instance}", "Instances that moved, whose shadows and light are drawn again.");
    private static readonly Counter<long> _giRays = Meter.CreateCounter<long>("magic.gems.cluster_rendering.gi.rays", "{ray}", "Rays the global illumination probes traced.");

    private readonly IGpu _gpu;
    private readonly IDebugUI? _debug;
    private readonly Threads _threads;
    private readonly ClusterRenderingSettings _settings;
    private readonly Assets _assets;

    /// <summary>
    /// The overlay pipeline run when the settings name none: whichever gem or project has it.
    /// </summary>
    private const string OverlayPath = "Pipelines/Overlay.pipeline";

    /// <summary>
    /// The pipeline a frame runs when the settings name none.
    /// </summary>
    private readonly Handle<Pipeline> _defaultPipeline;
    private readonly Snapshot _snapshot;
    private readonly TexturePools _textures;
    private readonly ClusterPool _models;
    private readonly Materials _materials;
    private readonly Scene _scene;
    private readonly SceneLights _lights;
    private readonly Shadows _shadows;
    private readonly PassGraph _graph;
    private readonly PassRecorder _recorder;
    private readonly Views _views;
    private readonly Output _output;
    private readonly Failures _failures;
    private readonly ResourceSet _frame;

    /// <summary>
    /// A target's names while its overlay stage records, the target itself among them; one set, each target in turn.
    /// </summary>
    private readonly ResourceSet _target;
    private readonly Dictionary<GpuTexture, ViewTarget> _targets = [];
    private readonly MeshFields _fields;
    private readonly GiVolume _gi;
    private readonly GpuArray<uint> _zBinsEmpty;
    private readonly GpuArray<uint> _zeros;
    private readonly GpuArray<Vector4> _moved;
    private readonly List<Subscription> _watching = [];
    private readonly List<SurfaceKey> _raster = [];
    private readonly HashSet<GpuTexture> _cleared = [];

    /// <summary>
    /// The domains being taken in before they spawn, each checked every frame until all it draws is in
    /// (<see cref="CheckPreloads"/>). Render thread.
    /// </summary>
    private readonly List<Preload> _preloads = [];

    /// <summary>
    /// The effects the latest domain taken in names, which the frame is built for while the world names none, so
    /// the listing the domain needs is compiled before it spawns; none once that domain is let go.
    /// </summary>
    private Handle<Pipeline> _preloadEffects;

    /// <summary>
    /// The debug view shown in place of the finished image, as last told (<see cref="DebugViewChanged"/>); empty for
    /// none. Render thread.
    /// </summary>
    private string _debugView = string.Empty;

    /// <summary>
    /// The debug view named that has no pass, said once.
    /// </summary>
    private string _missingView = string.Empty;

    public ClusterRenderingGem(IGpu gpu, IShaderCompiler compiler, Assets assets, Caches caches, World world, Events events, Threads threads, Lifetime lifetime, ClusterRenderingSettings settings, IWindow? window = null, IDebugUI? debug = null, IScripts? scripts = null)
    {
        _gpu = gpu;
        _debug = debug;
        _threads = threads;
        _settings = settings;
        _assets = assets;
        CancellationToken stopping = lifetime.Stopping;
        _snapshot = new Snapshot(world);
        _textures = new TexturePools(gpu, assets, caches, threads, settings, stopping);
        _fields = new MeshFields(gpu, assets, caches);
        _models = new ClusterPool(gpu, assets, threads, _textures, _fields, settings, stopping);
        _defaultPipeline = assets.Default<Pipeline>();
        _graph = new PassGraph(gpu, compiler, assets, scripts, threads, stopping);
        _materials = new Materials(gpu, assets, _textures, _graph, threads, stopping);
        _scene = new Scene(gpu, assets, _models, _materials);
        _lights = new SceneLights(gpu);
        _shadows = new Shadows(gpu);
        _recorder = new PassRecorder(gpu, _graph, _materials);
        _views = new Views(gpu, window, assets, threads, _scene, _models, _materials, settings, stopping);
        _output = new Output(gpu, compiler, threads, stopping);
        _failures = new Failures(gpu);
        _frame = new ResourceSet(gpu);
        _target = new ResourceSet(gpu);
        _gi = new GiVolume(gpu);
        _zBinsEmpty = new GpuArray<uint>(gpu, GpuBufferUsage.ComputeRead, Views.ZBinCount * 2);
        _zeros = new GpuArray<uint>(gpu, GpuBufferUsage.ComputeRead, 16384);
        _moved = new GpuArray<Vector4>(gpu, GpuBufferUsage.ComputeRead, 256);
        for (int bin = 0; bin < Views.ZBinCount; bin++)
        {
            _zBinsEmpty.Set(bin * 2, uint.MaxValue);
        }

        _watching.Add(events.Watch<AssetReloaded<Model>>(reloaded => OnRender(() => _models.Reload(reloaded.Asset))));
        _watching.Add(events.Watch<AssetReloaded<Texture>>(reloaded => OnRender(() => _textures.Reload(reloaded.Asset))));
        _watching.Add(events.Watch<AssetReloaded<Material>>(reloaded => OnRender(() => _materials.Reload(reloaded.Asset))));
        _watching.Add(events.Watch<AssetReloaded<Shader>>(reloaded => OnRender(() =>
        {
            _materials.Reload(reloaded.Asset);
            _graph.Invalidate();
        })));
        _watching.Add(events.Watch<AssetReloaded<Pipeline>>(_ => OnRender(_graph.Invalidate)));
        _watching.Add(events.Watch<AssetReloaded<Curve>>(_ => OnRender(_graph.Invalidate)));
        _watching.Add(events.Watch<AssetReloaded<Script>>(_ => OnRender(_graph.Invalidate)));
        _watching.Add(events.Watch<DebugViewChanged>(changed => OnRender(() => _debugView = changed.Name)));
    }

    public void Dispose()
    {
        foreach (Subscription watch in _watching)
        {
            watch.Dispose();
        }

        _snapshot.Dispose();
        _frame.Dispose();
        _target.Dispose();
        _gi.Dispose();
        _failures.Dispose();
        _output.Dispose();
        _views.Dispose();
        _recorder.Dispose();
        _graph.Dispose();
        _shadows.Dispose();
        _lights.Dispose();
        _scene.Dispose();
        _materials.Dispose();
        _models.Dispose();
        _fields.Dispose();
        _textures.Dispose();
        _zBinsEmpty.Dispose();
        _zeros.Dispose();
        _moved.Dispose();
    }

    public int Version => MeshFields.CacheVersion;

    /// <summary>
    /// A model's field as the cache keeps it: its box's corner and size, then its distances.
    /// </summary>
    public void Write(ModelField value, Stream bytes)
    {
        using BinaryWriter writer = new(bytes, Encoding.UTF8, leaveOpen: true);
        writer.Write(value.Min.X);
        writer.Write(value.Min.Y);
        writer.Write(value.Min.Z);
        writer.Write(value.Size.X);
        writer.Write(value.Size.Y);
        writer.Write(value.Size.Z);
        writer.Write(MemoryMarshal.AsBytes(value.Distances.AsSpan()));
    }

    public Result<ModelField> Read(Stream bytes)
    {
        using BinaryReader reader = new(bytes, Encoding.UTF8, leaveOpen: true);
        Vector3 min = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        Vector3 size = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        Half[] distances = new Half[MeshFields.Resolution * MeshFields.Resolution * MeshFields.Resolution];
        bytes.ReadExactly(MemoryMarshal.AsBytes(distances.AsSpan()));
        return Result.Success(new ModelField(min, size, distances));
    }

    int ICache<CompressedTexture>.Version => TexturePools.CacheVersion;

    /// <summary>
    /// A texture's levels as the cache keeps them: how many, then each one's length and its bytes.
    /// </summary>
    void ICache<CompressedTexture>.Write(CompressedTexture value, Stream bytes)
    {
        using BinaryWriter writer = new(bytes, Encoding.UTF8, leaveOpen: true);
        writer.Write(value.Levels.Length);
        foreach (byte[] level in value.Levels)
        {
            writer.Write(level.Length);
            writer.Write(level);
        }
    }

    Result<CompressedTexture> ICache<CompressedTexture>.Read(Stream bytes)
    {
        using BinaryReader reader = new(bytes, Encoding.UTF8, leaveOpen: true);
        byte[][] levels = new byte[reader.ReadInt32()][];
        for (int level = 0; level < levels.Length; level++)
        {
            levels[level] = reader.ReadBytes(reader.ReadInt32());
        }

        return Result.Success(new CompressedTexture(levels));
    }

    /// <summary>
    /// The copy is done: what is gone and what outgrew it is taken in, after the systems that copied the world ran in
    /// the ECS's LateUpdate, before this.
    /// </summary>
    public void LateUpdate(in Frame frame)
    {
        _snapshot.Finish();
    }

    /// <summary>
    /// Hands the copy to Render, now that the render thread is done with the one before.
    /// </summary>
    public void Sync(in Frame frame)
    {
        _snapshot.Swap();
    }

    /// <summary>
    /// Takes in what changed, places the sun's shadow about the main camera, then for every camera with somewhere to draw
    /// records the frame's stages once (probes), each view's stages, and the view shown in its target: up to the stage
    /// the debug view names, and what that stage made shown in place of the finished image. A window nothing is drawn
    /// into, with no camera on it or while the frame's passes first compile, is cleared to black.
    /// </summary>
    public void Render(in Frame frame)
    {
        _textures.FollowSettings();
        WorldCopy copy = _snapshot.Front;
        CameraCopy? main = MainCamera(copy);
        Vector3? player = copy.Player ?? main?.Transform.Position;
        _scene.Update(copy, (float)frame.Seconds, player ?? default);
        _movedInstances.Add(_scene.Moved.Count);
        _lights.Update(copy);
        if (main is { } camera)
        {
            _shadows.Plan(camera.Transform.Position, copy.Sun?.Direction ?? -Vector3.UnitY, _lights.ChangedLights.Capacity, _scene.Moved.Count, frame.Index, _settings);
        }

        ReportFailures();
        _models.Flush();
        _materials.Flush();
        DrawnMaterial? sky = copy.Sky is { } material && material != Handle<Material>.None ? _materials.Get(material) : null;
        SurfaceKey skySurface = sky is { } drawnSky ? drawnSky.Surface : default;
        _raster.Clear();
        _raster.AddRange(Materials.FixedRasterKeys);
        _raster.AddRange(_materials.RasterBins);
        _graph.Append(DebugViewPass(_debugView));
        _graph.Update(PipelineOf(), EffectsOf(copy), OverlayOf(), new SceneVariants(_raster, _materials.ResolveBins, _materials.TransparentSurfaces, skySurface));
        CheckPreloads(copy);
        if (_graph.Current?.Runs("GiField") == true && main is { } eye)
        {
            _gi.Plan(eye.Transform.Position, _scene.Moved, frame.DeltaSeconds, _settings);
            _giRays.Add(_gi.Rays);
        }
        else
        {
            _gi.Clear();
        }

        Counts counts = new(
            Instances: (uint)_scene.Capacity,
            Pieces: (uint)Math.Max(_settings.PieceCapacity, 1024),
            Clusters: (uint)Math.Max(_settings.PieceCapacity, 1024),
            // Room for every instance eight times over, so a frame drawing several of the shadow pages that hold the
            // whole world, the sun's coarsest levels' after a turn, fits rather than drawing nothing and waiting.
            Jobs: (uint)Math.Max(Math.Max(_settings.PieceCapacity / 16, 8 * _scene.Capacity), 1024),
            RasterBins: Materials.RasterBinRows,
            ResolveBins: (uint)Math.Max(_materials.ResolveBins.Count, 1),
            Tiles: 0,
            HizTexels: 0,
            HizLevels: 0,
            Lights: BitOperations.RoundUpToPowerOf2((uint)Math.Clamp(_settings.LightCapacity, 32, 1 << 20)),
            ZBins: Views.ZBinCount,
            LightTileWords: 0,
            GiRays: _gi.Rays,
            GiProbeThreads: _gi.ProbeThreads,
            SceneLights: (uint)_lights.Capacity,
            SunEntries: (uint)_shadows.SunEntries,
            LightEntries: Shadows.LightTableEntries,
            ShadowViews: (uint)Math.Max(_settings.ShadowUpdates, 1),
            ShadowGroups: (uint)(Math.Max(_settings.ShadowUpdates, 1) + _settings.ShadowLevels),
            ShadowCandidates: (uint)(_shadows.WholePages + _shadows.SmallPages));
        List<View> views = _views.Prepare(new ViewFrame(copy, frame, sky?.Row ?? 0, counts, player, Jitters()));
        _cleared.Clear();
        if (_graph.Current is not { } listing)
        {
            ClearWindow();
            _textures.EndFrame();
            return;
        }

        if (views.Count == 0)
        {
            ClearWindow();
            RecordOverlays(listing, views, null, frame);
            _textures.EndFrame();
            return;
        }

        View shown = views.Find(view => view.Target.IsWindow) ?? views[0];
        SetFrame(main?.Transform.Position ?? shown.Position);
        _failures.Say(listing.Failures);
        _failures.Begin(_zeros.Buffer);
        RecordScope frameScope = new(_frame, null, shown.Constants, shown.Counts, [], GpuBuffer.None, skySurface, frame);
        for (PassStage stage = PassStage.Probes; stage < PassStage.Cull; stage++)
        {
            _recorder.Record(listing, stage, frameScope);
        }

        foreach (View view in views)
        {
            view.Resources.Keep(listing);
            RecordScope scope = new(_frame, view.Resources, view.Constants, view.Counts, view.TransparentRuns, view.TransparentPieces, skySurface, frame);
            for (PassStage stage = PassStage.Cull; stage <= PassStage.Ui; stage++)
            {
                _recorder.Record(listing, stage, scope);
            }

            BoundResource? image = _debugView.Length > 0 ? DebugImage(view) : Finished(view);
            bool clears = _cleared.Add(view.Target.Texture);
            if (image is { } made)
            {
                _output.Draw(view.Target, view.Constants, made, _settings.OutputFilter, clears, _failures);
            }
            else
            {
                _output.DrawNothing(view.Target, view.Constants, clears);
            }

            view.Resources.Swap();
        }

        _frame.Keep(listing);
        ClearWindow();
        RecordOverlays(listing, views, shown, frame);
        _textures.EndFrame();
    }

    /// <summary>
    /// Takes in what <paramref name="prefabs"/> draw before they spawn, so they are drawn whole in their first frame:
    /// their models into the pool, their materials packed with their textures in and every surface's pipelines
    /// compiled, and the frame built for the effects they name. Done once all of it is in, or failed, and so drawn as
    /// the failure; what it hands back keeps it in the pool until it is disposed.
    /// </summary>
    public async Task<Subscription> PreloadAsync(IReadOnlyList<Prefab> prefabs, IProgress<float> progress, CancellationToken cancel)
    {
        Preloaded preloaded = Preloaded.Of(prefabs);
        Preload preload = new(preloaded, progress);
        using CancellationTokenRegistration cancelling = cancel.Register(() => OnRender(() => Cancel(preload, cancel)));
        OnRender(() => Begin(preload));
        await preload.Done.Task.ConfigureAwait(false);
        return new Subscription(() => OnRender(() => End(preloaded)));
    }

    /// <summary>
    /// Records the overlay stage once for each target drawn into this frame, the window among them whether or not a
    /// view draws into it: each is <c>Target</c>, with its own size, format and encoding in the constants, over the
    /// view shown in it, or over black. The constants are <paramref name="shown"/>'s, the view in the window, else the
    /// frame's alone.
    /// </summary>
    private void RecordOverlays(Listing listing, List<View> views, View? shown, in Frame frame)
    {
        _target.Keep(listing);
        _targets.Clear();
        GpuTexture window = _views.Window();
        System.Drawing.Size size = window == GpuTexture.None ? System.Drawing.Size.Empty : _gpu.SizeOf(window);
        if (!size.IsEmpty)
        {
            _targets[window] = new ViewTarget(window, new System.Drawing.Rectangle(System.Drawing.Point.Empty, size), _gpu.FormatOf(window), true);
        }

        foreach (View view in views)
        {
            if (!view.Target.IsWindow)
            {
                _targets.TryAdd(view.Target.Texture, view.Target);
            }
        }

        foreach (ViewTarget target in _targets.Values)
        {
            _target.Set(ResourceInfo.TargetName, BoundResource.Of(target.Texture, (uint)target.Area.Width, (uint)target.Area.Height));
            FrameConstants constants = shown?.Constants ?? default;
            constants.ViewSize = new Vector2(target.Area.Width, target.Area.Height);
            constants.ViewTexel = new Vector2(1f / target.Area.Width, 1f / target.Area.Height);
            constants.OutputSize = constants.ViewSize;
            constants.TargetEncoding = (uint)target.Encoding;
            constants.Time = (float)frame.Seconds;
            constants.FrameIndex = (uint)(frame.Index % uint.MaxValue);
            RecordScope scope = new(_frame, _target, constants, shown?.Counts ?? default, [], GpuBuffer.None, default, frame, target.Format, target.Encoding);
            _recorder.Record(listing, PassStage.Overlay, scope);
        }
    }

    /// <summary>
    /// The overlay pipeline the settings name, else the one at <see cref="OverlayPath"/>, else none; changed while
    /// running, the next listing built runs it.
    /// </summary>
    private Handle<Pipeline> OverlayOf()
    {
        return _settings.Overlay == Handle<Pipeline>.None ? _assets.Find<Pipeline>(OverlayPath) : _settings.Overlay;
    }

    /// <summary>
    /// The pipeline the settings name, or the default one; changed while running, the next listing built runs it.
    /// </summary>
    private Handle<Pipeline> PipelineOf()
    {
        return _settings.Pipeline == Handle<Pipeline>.None ? _defaultPipeline : _settings.Pipeline;
    }

    /// <summary>
    /// Clears the window to black when no view drew into it this frame, so it never shows what its image held before.
    /// </summary>
    private void ClearWindow()
    {
        GpuTexture window = _views.Window();
        if (window != GpuTexture.None && _cleared.Add(window))
        {
            _output.Clear(window);
        }
    }

    /// <summary>
    /// The frame's names this frame: the scene's tables, as they may have grown, the materials' texture pools, where it
    /// moved relative to the main camera, which the shadows are placed about, the templates binning and counting start
    /// from, and the shadows' pool and tables.
    /// </summary>
    private void SetFrame(Vector3 main)
    {
        for (int i = 0; i < _scene.Moved.Count; i++)
        {
            MovedSphere sphere = _scene.Moved[i];
            _moved.Set(i, new Vector4(sphere.Centre - main, sphere.Radius));
        }

        _moved.Flush();
        _zBinsEmpty.Flush();
        _zeros.Flush();
        _frame.Set("Instances", BoundResource.Of(_scene.Instances.Buffer, _scene.Instances.Bytes));
        _frame.Set("Models", BoundResource.Of(_models.Models.Buffer, _models.Models.Bytes));
        _frame.Set("Levels", BoundResource.Of(_models.Levels.Buffer, _models.Levels.Bytes));
        _frame.Set("Clusters", BoundResource.Of(_models.Clusters.Buffer, _models.Clusters.Bytes));
        _frame.Set("Cards", BoundResource.Of(_models.Cards.Buffer, _models.Cards.Bytes));
        _frame.Set("Geometry", BoundResource.Of(_models.Geometry, 4));
        _frame.Set("Materials", BoundResource.Of(_materials.Records.Buffer, _materials.Records.Bytes));
        _frame.Set("MaterialBins", BoundResource.Of(_materials.Bins.Buffer, _materials.Bins.Bytes));
        for (int pool = 0; pool < TexturePools.Count; pool++)
        {
            _frame.Set(TexturePools.Names[pool], BoundResource.Of(_textures.TextureOf(pool), 0, 0, _textures.Sampler));
        }

        _frame.Set("Moved", BoundResource.Of(_moved.Buffer, _moved.Bytes));
        _frame.Set("ZBinsEmpty", BoundResource.Of(_zBinsEmpty.Buffer, _zBinsEmpty.Bytes));
        _frame.Set("Zeros", BoundResource.Of(_zeros.Buffer, _zeros.Bytes));
        _frame.Set("Failures", BoundResource.Of(_failures.Raised, Failures.RaisedBytes));
        _frame.Set("MeshFieldAtlas", BoundResource.Of(_fields.Atlas, MeshFields.Resolution, MeshFields.Resolution));
        _frame.Set("MeshFields", BoundResource.Of(_fields.Table.Buffer, _fields.Table.Bytes));
        _frame.Set("MaterialGi", BoundResource.Of(_materials.Gi.Buffer, _materials.Gi.Bytes));
        _frame.Set("GiLevels", BoundResource.Of(_gi.Rows.Buffer, _gi.Rows.Bytes));
        _frame.Set("SceneLights", BoundResource.Of(_lights.Rows.Buffer, _lights.Rows.Bytes));
        _frame.Set("ChangedLights", BoundResource.Of(_lights.ChangedLights.Buffer, _lights.ChangedLights.Bytes));
        _shadows.Bind(_frame);
    }

    /// <summary>
    /// Whether views are drawn a sub-pixel off each frame, which the temporal upscale resolves into detail finer than a
    /// pixel: only while the frame runs it, as nothing else would undo the shake, and not while a debug view is shown,
    /// which is drawn from the view as it is and would shake with it.
    /// </summary>
    private bool Jitters() => _graph.Current?.Runs("Taa") == true && _debugView.Length == 0;

    /// <summary>
    /// Says, at each instance drawn as the failure, why, with what failed while the mouse rests on it: for this frame,
    /// so the words go once it is repaired. Only while the debug UI shows.
    /// </summary>
    private void ReportFailures()
    {
        if (_debug is not { Enabled: true } debug)
        {
            return;
        }

        foreach ((int slot, (string why, string what)) in _scene.Failures)
        {
            debug.Error(why, _scene.PositionOf(slot), 0, what);
        }
    }

    /// <summary>
    /// The pass of the debug view <paramref name="name"/> names, <c>Passes/Debug/Name.pass</c>; none for no view, or for
    /// a name no pass has, which is said once.
    /// </summary>
    private Handle<Pass> DebugViewPass(string name)
    {
        if (name.Length == 0)
        {
            return Handle<Pass>.None;
        }

        Handle<Pass> pass = _assets.Find<Pass>($"Passes/Debug/{name}.pass");
        if (pass == Handle<Pass>.None && !string.Equals(name, _missingView, StringComparison.Ordinal))
        {
            _missingView = name;
            Debugging.Warn($"There is no debug view {name}: no pass is Passes/Debug/{name}.pass, so the frame is shown as it is.");
        }

        return pass;
    }

    /// <summary>
    /// The view's finished image: what the post stage made, else the lit scene as it is; none until either is made.
    /// </summary>
    private static BoundResource Finished(View view)
    {
        return view.Resources.TryGet("Ldr", out BoundResource ldr) ? ldr : view.Resources.TryGet("Hdr", out BoundResource hdr) ? hdr : default;
    }

    /// <summary>
    /// What <paramref name="view"/> shows while a debug view is named: the texture its pass drew this frame, null while
    /// the pass is not ready or does not run, so what is shown says that nothing draws, never an image from an earlier
    /// frame.
    /// </summary>
    private static BoundResource? DebugImage(View view)
    {
        return view.Resources.TryGet("DebugView", out BoundResource drawn) && drawn.Texture != GpuTexture.None ? drawn : null;
    }

    /// <summary>
    /// The camera drawing into the window, else the first; none without a camera.
    /// </summary>
    private static CameraCopy? MainCamera(WorldCopy copy)
    {
        foreach (CameraCopy camera in copy.Cameras)
        {
            if (camera.Camera.Target.IsWindow)
            {
                return camera;
            }
        }

        return copy.Cameras.Count > 0 ? copy.Cameras[0] : null;
    }

    private void OnRender(Action work)
    {
        _threads.Post(EngineThread.Render, work);
    }

    /// <summary>
    /// The effects the frame is built for: the world's, else those of the latest domain being taken in, so its
    /// listing is compiled under the loading domain, which names none of its own.
    /// </summary>
    private Handle<Pipeline> EffectsOf(WorldCopy copy)
    {
        return copy.Effects == Handle<Pipeline>.None ? _preloadEffects : copy.Effects;
    }

    /// <summary>
    /// Starts taking in what a domain draws: its models and materials are held, then asked for as the nearest, which
    /// starts their loads, and the frame is built for its effects.
    /// </summary>
    private void Begin(Preload preload)
    {
        Preloaded preloaded = preload.Preloaded;
        _scene.Hold(preloaded);
        foreach (Handle<Model> model in preloaded.Models)
        {
            _models.RowOf(model, 0);
        }

        foreach (Handle<Material> material in preloaded.Materials)
        {
            _materials.RowOf(material);
        }

        if (preloaded.Effects != Handle<Pipeline>.None)
        {
            _preloadEffects = preloaded.Effects;
        }

        _preloads.Add(preload);
    }

    /// <summary>
    /// A domain that was taken in is let go: what no look draws leaves, and the frame no longer builds for its effects.
    /// </summary>
    private void End(Preloaded preloaded)
    {
        _scene.Release(preloaded);
        if (preloaded.Effects == _preloadEffects)
        {
            _preloadEffects = Handle<Pipeline>.None;
        }
    }

    /// <summary>
    /// The open of a domain being taken in was cancelled before all it draws was in: it is let go now, and whoever
    /// waited is told; one already in is let go by whoever holds it.
    /// </summary>
    private void Cancel(Preload preload, CancellationToken cancel)
    {
        if (_preloads.Remove(preload))
        {
            End(preload.Preloaded);
            preload.Done.TrySetCanceled(cancel);
        }
    }

    /// <summary>
    /// Checks each domain being taken in: each model that can be drawn, or failed and is drawn as the failure cube,
    /// each material shown, and the frame built for the effects, tell how far it is; once all are in, whoever waits
    /// for it goes on and spawns it.
    /// </summary>
    private void CheckPreloads(WorldCopy copy)
    {
        if (_preloads.Count == 0)
        {
            return;
        }

        bool isBuilt = _graph.Current is not null && !_graph.IsBuilding && _graph.Effects == EffectsOf(copy);
        for (int i = _preloads.Count - 1; i >= 0; i--)
        {
            Preload preload = _preloads[i];
            Preloaded preloaded = preload.Preloaded;
            int total = 1 + preloaded.Models.Count + preloaded.Materials.Count;
            int done = isBuilt ? 1 : 0;
            foreach (Handle<Model> model in preloaded.Models)
            {
                if (_models.IsDrawable(model))
                {
                    done++;
                }
            }

            foreach (Handle<Material> material in preloaded.Materials)
            {
                if (_materials.IsShown(_materials.RowOf(material)))
                {
                    done++;
                }
            }

            float share = (float)done / total;
            if (share != preload.Reported)
            {
                preload.Reported = share;
                preload.Progress.Report(share);
            }

            if (done == total)
            {
                _preloads.RemoveAt(i);
                preload.Done.TrySetResult();
            }
        }
    }

    /// <summary>
    /// A domain being taken in before it spawns: what it draws, how far that is as last told, and the task whoever
    /// opens it awaits.
    /// </summary>
    private sealed class Preload(Preloaded preloaded, IProgress<float> progress)
    {
        public Preloaded Preloaded { get; } = preloaded;

        public IProgress<float> Progress { get; } = progress;

        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public float Reported { get; set; } = -1;
    }
}
