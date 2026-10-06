using Magic.Contexts;
using Magic.Attributes.Scripts;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Contexts.Events;
using Magic.Contexts.Rendering;
using Magic.Contexts.Settings;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Services;
using Magic.Utils;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

namespace DeferredRendererGem;

/// <summary>
/// Draws the entities through whatever <see cref="IRendering"/> is loaded, which is only the GPU: everything else (the GPU
/// tables, shaders and pipelines, culling, lighting, the pipeline's passes, hot reload) is here, its state in one
/// <see cref="RenderContext"/>. <see cref="Run"/> runs in Render, after LateUpdate and the transforms, one way, top to bottom:
/// asset changes reload; the entities are synced into the tables (every <see cref="Renderer"/> that has a
/// <see cref="WorldTransform"/> and no <see cref="RenderInstance"/> yet is registered once its model and materials
/// have been loaded, which happens off this thread (<see cref="Preloads"/>) and is not waited for, and only as
/// many a frame as fit in <see cref="RegisterBudgetMs"/> and <see cref="GeometryBudgetBytes"/>, the ones whose
/// <see cref="Renderer"/> went away are forgotten and the ones set again are registered again, both told by observers so
/// nothing is swept, and the non-static ones give their world matrices); what changed is uploaded; the frame is planned;
/// and the plan is recorded into <see cref="Frame.DrawCommands"/>. Then the gems loaded after the ECS may add their own
/// commands on top in their Render hook and the host submits the list, leaving how long that took in
/// <see cref="Debugging.Stats"/> with this system's own numbers. The renderer gem is static, so the one given here is
/// kept; without one nothing is drawn.
/// </summary>
[Phase(UpdateType.Render)]
internal sealed class RenderSystem : ISystem
{
    /// <summary>
    /// Milliseconds a frame may spend registering new entities. The ones it does not get to wait, undrawn, for the
    /// next frame, so a chunk that just spawned comes in over a few frames instead of holding one up.
    /// </summary>
    private const double RegisterBudgetMs = 2;

    /// <summary>
    /// Bytes of new geometry a frame may take on: every model seen for the first time is uploaded in the frame it is
    /// registered in, and a few hundred megabytes at once (the stand-ins of every far chunk) stall it.
    /// </summary>
    private const long GeometryBudgetBytes = 8 * 1024 * 1024;

    /// <summary>
    /// How many waiting entities a frame looks at: more than <see cref="RegisterBudgetMs"/> ever lets in. Copying out all
    /// of them while a hundred thousand stream in cost more than registering the few hundred that fit.
    /// </summary>
    private const int RegisterCandidates = 1024;

    private readonly IEcs _ecs;
    private readonly Assets _assets;
    private readonly IFileSystem _files;
    private readonly Project _project;
    private readonly Settings _values;
    private readonly LodSettings _lod;
    private readonly DeferredSettings _settings;
    private readonly World _world;
    private readonly IWindowRegistry? _windows;
    private readonly IRendering? _rendering;
    private readonly Threads _threads;
    private readonly IEcsQuery<Renderer, WorldTransform> _unregistered;
    private readonly IEcsQuery<WorldTransform, RenderInstance> _movable;
    private readonly IEcsQuery<RenderInstance> _allInstances;
    private readonly IEcsQuery<Camera, WorldTransform> _cameras;
    private readonly IEcsQuery<PostProcessing> _postProcessing;
    private readonly IEcsQuery<Sky> _skies;
    private readonly IEcsQuery<DirectionalLight, WorldTransform> _directional;
    private readonly IEcsQuery<PointLight, WorldTransform> _points;
    private readonly IEcsQuery<SpotLight, WorldTransform> _spots;
    private readonly IEcsQuery<AreaLight, WorldTransform> _areas;
    private readonly IEcsQuery<Glow, WorldTransform> _glowing;
    private readonly IEcsQuery<DirectionalLight, WorldTransform> _directionalSettled;
    private readonly IEcsQuery<PointLight, WorldTransform> _pointsSettled;
    private readonly IEcsQuery<SpotLight, WorldTransform> _spotsSettled;
    private readonly IEcsQuery<AreaLight, WorldTransform> _areasSettled;
    private readonly IEcsQuery<Glow, WorldTransform> _glowingSettled;
    private readonly IDisposable[] _onSettledChanged;
    private readonly IDisposable _onSet;
    private readonly IDisposable _onRemoved;
    private readonly IDisposable _onHidden;
    private readonly IDisposable _onShown;

    // Queued by the observers (which must not change the world), applied at the start of the next pass.
    private readonly List<(Handle Entity, RenderInstance Instance)> _removed = [];
    private readonly List<Handle> _changed = [];
    private readonly List<Handle> _hiddenChanged = [];

    private readonly List<(Handle Entity, Renderer Renderer, Matrix4x4 World)> _toRegister = [];
    private readonly List<View> _views = [];
    private readonly List<LightInstance> _lights = [];
    private readonly List<GpuGlow> _glows = [];

    // The lights and glows that never move (static, their place computed): read when one of them changes, not every frame.
    private readonly List<LightInstance> _settledLights = [];
    private readonly List<GpuGlow> _settledGlows = [];
    private List<LightInstance> _collectedLights;
    private List<GpuGlow> _collectedGlows;
    private bool _settledChanged = true;
    private readonly List<Handle> _toStrip = [];
    private readonly List<(ulong Id, Preloaded Loaded)> _reloaded = [];
    private readonly FramePlan _plan = new();

    // One delegate each, not one per frame.
    private readonly QueryChunkAction<Renderer, WorldTransform> _collectUnregistered;
    private readonly QueryChunkAction<WorldTransform, RenderInstance> _move;
    private readonly QueryChunkAction<Camera, WorldTransform> _collectViews;
    private readonly QueryChunkAction<PostProcessing> _collectPosts;
    private readonly QueryChunkAction<Sky> _collectSky;
    private readonly QueryChunkAction<DirectionalLight, WorldTransform> _collectDirectional;
    private readonly QueryChunkAction<PointLight, WorldTransform> _collectPoints;
    private readonly QueryChunkAction<SpotLight, WorldTransform> _collectSpots;
    private readonly QueryChunkAction<AreaLight, WorldTransform> _collectAreas;
    private readonly QueryChunkAction<Glow, WorldTransform> _collectGlows;

    private uint? _builtFor; // the device generation the context was built for; null until the first run
    private IReadOnlyDictionary<string, System.Text.Json.JsonElement>? _appliedValues; // the settings values the passes were made with
    private PostList _posts;       // the world's post-processing this frame; empty without one
    private int _postLists;        // how many entities carry a PostProcessing this frame
    private bool _warnedPostLists;
    private Vector3? _sky;         // the world's sky colour this frame; null without a Sky entity
    private int _skyCount;           // how many entities carry a Sky this frame
    private bool _warnedSkies;
    private Handle _lastParent;    // of the entity registered last, and whether it or anything above it is hidden:
    private bool _lastParentHidden; // a chunk's entities share one, so it is looked up once a frame, not once each.
                                    // Lights and glows ask once per span (one table: one parent), not per entity

    public RenderSystem(IEcs ecs, Assets assets, IFileSystem files, Project project, Settings values, LodSettings lod, DeferredSettings settings, World world, IWindowRegistry? windows, IRendering? rendering, Threads threads)
    {
        _threads = threads;
        _ecs = ecs;
        _assets = assets;
        _files = files;
        _project = project;
        _values = values;
        _lod = lod;
        _settings = settings;
        _world = world;
        _windows = windows;
        _rendering = rendering;
        _unregistered = ecs.Query<Renderer, WorldTransform>().Without<RenderInstance>().Build();
        _movable = ecs.Query<WorldTransform, RenderInstance>().Without<Settled>().Build(); // a settled static never moves
        _allInstances = ecs.Query<RenderInstance>().Build();
        _cameras = ecs.Query<Camera, WorldTransform>().Build();
        _postProcessing = ecs.Query<PostProcessing>().Build();
        _skies = ecs.Query<Sky>().Build();
        _directional = ecs.Query<DirectionalLight, WorldTransform>().Without<Settled>().Build();
        _points = ecs.Query<PointLight, WorldTransform>().Without<Settled>().Build();
        _spots = ecs.Query<SpotLight, WorldTransform>().Without<Settled>().Build();
        _areas = ecs.Query<AreaLight, WorldTransform>().Without<Settled>().Build();
        _glowing = ecs.Query<Glow, WorldTransform>().Without<Settled>().Build();
        _directionalSettled = ecs.Query<DirectionalLight, WorldTransform>().With<Settled>().Build();
        _pointsSettled = ecs.Query<PointLight, WorldTransform>().With<Settled>().Build();
        _spotsSettled = ecs.Query<SpotLight, WorldTransform>().With<Settled>().Build();
        _areasSettled = ecs.Query<AreaLight, WorldTransform>().With<Settled>().Build();
        _glowingSettled = ecs.Query<Glow, WorldTransform>().With<Settled>().Build();
        _collectedLights = _lights;
        _collectedGlows = _glows;

        // Anything that can change what the settled lights and glows are: one settling or unsettling, one set or taken
        // away. Entities hidden or shown are seen through the hidden observers.
        void Changed(Handle entity) => _settledChanged = true;
        _onSettledChanged =
        [
            ecs.Observe<Settled>(ComponentEvent.Added, Changed), ecs.Observe<Settled>(ComponentEvent.Removed, Changed),
            ecs.Observe<DirectionalLight>(ComponentEvent.Set, Changed), ecs.Observe<DirectionalLight>(ComponentEvent.Removed, Changed),
            ecs.Observe<PointLight>(ComponentEvent.Set, Changed), ecs.Observe<PointLight>(ComponentEvent.Removed, Changed),
            ecs.Observe<SpotLight>(ComponentEvent.Set, Changed), ecs.Observe<SpotLight>(ComponentEvent.Removed, Changed),
            ecs.Observe<AreaLight>(ComponentEvent.Set, Changed), ecs.Observe<AreaLight>(ComponentEvent.Removed, Changed),
            ecs.Observe<Glow>(ComponentEvent.Set, Changed), ecs.Observe<Glow>(ComponentEvent.Removed, Changed),
        ];
        _onSet = ecs.Observe<Renderer>(ComponentEvent.Set, OnRendererSet);
        _onRemoved = ecs.Observe<Renderer>(ComponentEvent.Removed, OnRendererRemoved);
        _onHidden = ecs.Observe<Hidden>(ComponentEvent.Added, _hiddenChanged.Add);
        _onShown = ecs.Observe<Hidden>(ComponentEvent.Removed, _hiddenChanged.Add);
        _collectUnregistered = CollectUnregistered;
        _move = Move;
        _collectViews = CollectViews;
        _collectPosts = CollectPosts;
        _collectSky = CollectSky;
        _collectDirectional = (entities, lights, worlds) => CollectLights(entities, lights, worlds, LightInstance.Of);
        _collectPoints = (entities, lights, worlds) => CollectLights(entities, lights, worlds, LightInstance.Of);
        _collectSpots = (entities, lights, worlds) => CollectLights(entities, lights, worlds, LightInstance.Of);
        _collectAreas = (entities, lights, worlds) => CollectLights(entities, lights, worlds, LightInstance.Of);
        _collectGlows = CollectGlows;
    }

    public void Dispose()
    {
        _onSet.Dispose();
        _onRemoved.Dispose();
        _onHidden.Dispose();
        _onShown.Dispose();
        foreach (IDisposable observer in _onSettledChanged)
            observer.Dispose();
        _directionalSettled.Dispose();
        _pointsSettled.Dispose();
        _spotsSettled.Dispose();
        _areasSettled.Dispose();
        _glowingSettled.Dispose();
        _unregistered.Dispose();
        _movable.Dispose();
        _allInstances.Dispose();
        _cameras.Dispose();
        _postProcessing.Dispose();
        _skies.Dispose();
        _directional.Dispose();
        _points.Dispose();
        _spots.Dispose();
        _areas.Dispose();
        _glowing.Dispose();
    }

    /// <summary>
    /// The render state of the current renderer; null without one.
    /// </summary>
    public RenderContext? Context { get; private set; }

    /// <summary>
    /// Syncs the entities into the render state and records the scene into <paramref name="frame"/>'s commands, drawing into
    /// the windows of the <see cref="IWindowRegistry"/>. Without a renderer it does nothing.
    /// </summary>
    public void Run(in Frame frame)
    {
        long started = Stopwatch.GetTimestamp();
        if (_builtFor != (_rendering?.Generation ?? 0))
            RebuildContext();

        if (Context is not { } ctx)
            return;

        ctx.Textures.UseAnisotropy(ctx.Gpu, _settings.Anisotropy);

        // A preset was applied: every pass takes its values again.
        if (!ReferenceEquals(_appliedValues, _values.Values))
        {
            if (_appliedValues is not null)
                PassLoader.Reapply(ctx);
            _appliedValues = _values.Values;
        }

        // The previous frame was submitted and nothing of this one is uploaded yet: in a debugging renderer, when asked
        // for, the periodic culling check of what it drew.
        if (ctx.Gpu.Debug && ctx.Deferred.CullingCheck && frame.Number % RenderChecks.VerifyEveryFrames == 0 && _plan.Views.Count > 0)
        {
            RenderChecks.Verify(ctx, _plan.Views[0].Resources, _plan.Views[0].Constants);
            RenderChecks.VerifyShadowPlan(ctx, _plan.Views[_plan.MainView].Constants);
        }

        // Reload: what changed on disk, and the loads and compiles that finished.
        ApplyAssetChanges(ctx, frame.Events.Span);
        Preloads.Poll(ctx, frame.Number);
        ApplyReloads(ctx, frame.Number);
        ctx.Pipelines.Compiles.Poll(ctx, Pipelines.FinishCompile);
        ctx.Pipeline.Compiles.Poll(ctx, PassLoader.FinishCompile);

        // Sync: the entities into the tables. The moves read every chunk's columns as they are: no copy, and statics and
        // unchanged matrices are skipped.
        _settledChanged |= _hiddenChanged.Count > 0;
        Unregister(ctx);
        ApplyHidden(ctx);
        Reregister(ctx);
        Register(ctx);

        _movable.Run(_move);
        _views.Clear();
        _cameras.Run(_collectViews);
        CollectPostProcessing();
        CollectSkies();
        CollectSettled();
        _lights.Clear();
        _lights.AddRange(_settledLights);
        _directional.Run(_collectDirectional);
        _points.Run(_collectPoints);
        _spots.Run(_collectSpots);
        _areas.Run(_collectAreas);
        _glows.Clear();
        _glows.AddRange(_settledGlows);
        _glowing.Run(_collectGlows);

        // Upload: what the tables changed.
        Textures.Flush(ctx);
        Materials.Flush(ctx);
        Meshes.Flush(ctx);
        Instancing.Flush(ctx);
        Lights.UploadLights(ctx, CollectionsMarshal.AsSpan(_lights));
        Lights.UploadGlows(ctx, CollectionsMarshal.AsSpan(_glows));

        long recording = Stopwatch.GetTimestamp();
        double syncMs = Stopwatch.GetElapsedTime(started, recording).TotalMilliseconds;

        // Plan, then record: the plan is everything the commands are made from.
        Handle<Pipeline> pipeline = _world.Pipeline.IsValid ? _world.Pipeline : _assets.Find<Pipeline>(Pipeline.DefaultPath);
        Scene.Plan(ctx, _plan, _windows, CollectionsMarshal.AsSpan(_views), pipeline, ctx.Deferred.PostProcessing && ctx.Deferred.DebugView == DebugView.Normal ? _posts : default, LightingConstants.From(CollectionsMarshal.AsSpan(_lights), _sky), (float)frame.Time, frame.Number);
        FailureLabels.Show(ctx);
        (int draws, int dispatches) = Scene.Record(ctx, frame.DrawCommands, _plan);
        float recordMs = (float)Stopwatch.GetElapsedTime(recording).TotalMilliseconds;

        // The host submits after this, so Render.SubmitMs and Render.WaitMs (its stats) are the previous frame's.
        Debugging.Stats.Set("Frame.Time.RenderSyncMs", syncMs);
        Debugging.Stats.Set("Frame.Time.RenderRecordMs", recordMs);
        Debugging.Stats.Set("Rendering.Instances", ctx.Instances.Alive);
        Debugging.Stats.Set("Rendering.Lights", _lights.Count);
        Debugging.Stats.Set("Rendering.Draws", draws);
        Debugging.Stats.Set("Rendering.Dispatches", dispatches);
        Debugging.Stats.Set("Rendering.Resident.PipelinesCompiling", ctx.Pipelines.Pending);
        Debugging.Stats.Set("Rendering.Resident.Meshes", ctx.Meshes.MeshCount);
        Debugging.Stats.Set("Rendering.Resident.Textures", ctx.Textures.Count);
    }

    /// <summary>
    /// The first run, or the GPU device was made again (another backend): every instance registered with the old
    /// context is stale, and the old context goes with the old device's objects. No setting builds it again, since
    /// nothing here releases a context's GPU objects on the same device. A renderer whose built-in shaders do not
    /// compile draws nothing, logged once.
    /// </summary>
    private void RebuildContext()
    {
        if (_builtFor is not null)
            Debugging.Log.Info("The GPU device changed: the render state is built again.");

        StripInstances();
        Context = null;
        _plan.Clear();
        if (_rendering is not null)
        {
            try
            {
                Context = CreateContext(_rendering, _assets, _files, _project, _values, _lod, _settings, _threads);
            }
            catch (InvalidOperationException ex)
            {
                Debugging.Log.Error($"The renderer cannot draw the scene: {ex.Message}");
            }
        }

        _builtFor = _rendering?.Generation ?? 0;
    }

    /// <summary>
    /// A context for <paramref name="gpu"/>: the tables, the built-in shaders and the prewarmed failure pipelines. Throws
    /// when a built-in shader does not compile: nothing could be drawn.
    /// </summary>
    private static RenderContext CreateContext(IRendering gpu, Assets assets, IFileSystem files, Project project, Settings values, LodSettings lod, DeferredSettings settings, Threads threads)
    {
        GpuStructs.AssertLayout();
        Debugging.Log.Verbose($"Render settings: anisotropy {settings.Anisotropy}, scale {settings.Scale}, level of detail bias {lod.Bias}.");

        ulong defaultSurface = assets.Find<Shader>("Shaders/Surfaces/Pbr.surf.hlsl").Id;
        ulong failureSurface = assets.Find<Shader>("Shaders/Surfaces/Failure.surf.hlsl").Id;
        if (defaultSurface == 0)
            Debugging.Log.Error("Shaders/Surfaces/Pbr.surf.hlsl is not an indexed asset: there is no default material.");
        if (failureSurface == 0)
            Debugging.Log.Error("Shaders/Surfaces/Failure.surf.hlsl is not an indexed asset: broken materials and shaders will draw grey or not at all.");

        RenderContext ctx = new()
        {
            Gpu = gpu,
            Assets = assets,
            Files = files,
            Project = project,
            Threads = threads,
            Settings = values,
            Lod = lod,
            Deferred = settings,
            Shaders = new ShaderCache(Path.Combine(project.Resources, "Shaders"), Path.Combine(project.Cache, "Shaders", gpu.ShaderFormat), gpu.ShaderFormat, settings),
            Textures = new TextureTable(gpu, settings.Anisotropy),
            Meshes = new MeshTable(gpu, 1 << 22, 1 << 24),
            Materials = new MaterialTable(gpu, defaultSurface, failureSurface),
            Instances = new InstanceTable(gpu, 4096),
            Buckets = new Buckets(gpu, 1 << 22),
            LinearClamp = gpu.CreateSampler(new SamplerDesc(GpuFilter.Linear, GpuAddress.Clamp)),
            NearestClamp = gpu.CreateSampler(new SamplerDesc(GpuFilter.Nearest, GpuAddress.Clamp)),
            Comparison = gpu.CreateSampler(new SamplerDesc(GpuFilter.Linear, GpuAddress.Clamp, Compare: GpuCompare.GreaterOrEqual)),
            Lights = new GrowableBuffer(gpu, GpuBufferUsage.ComputeRead | GpuBufferUsage.ComputeWrite | GpuBufferUsage.GraphicsRead, 64 * GpuLight.Size),
            Glows = new GrowableBuffer(gpu, GpuBufferUsage.GraphicsRead, 64 * GpuGlow.Size),
            Counts = new GrowableBuffer(gpu, GpuBufferUsage.ComputeRead | GpuBufferUsage.GraphicsRead, GpuCounts.Size),
            StubDepth = gpu.CreateTexture(new TextureDesc(gpu.DepthFormat, GpuTextureUsage.DepthTarget | GpuTextureUsage.Sampler, 1, 1)),
            StubBuffer = gpu.CreateBuffer(GpuBufferUsage.ComputeRead | GpuBufferUsage.GraphicsRead, 16),
            StubVolume = gpu.CreateTexture(new TextureDesc(GpuFormat.Rgba8Unorm, GpuTextureUsage.Sampler | GpuTextureUsage.ComputeWrite, 8, 8, Layers: 8, Kind: GpuTextureKind.Texture3D)),
            BrickAtlas = gpu.CreateTexture(new TextureDesc(GpuFormat.R8Unorm, GpuTextureUsage.Sampler | GpuTextureUsage.ComputeWrite, GiBricks.BricksAcross * GiBricks.BrickSize, GiBricks.BricksAcross * GiBricks.BrickSize, Layers: GiBricks.BricksDeep * GiBricks.BrickSize, Kind: GpuTextureKind.Texture3D)),
            BrickJobs = new GrowableBuffer(gpu, GpuBufferUsage.ComputeRead, 64 * GpuBrickJob.Size),
            BrickArgs = new GrowableBuffer(gpu, GpuBufferUsage.ComputeRead | GpuBufferUsage.Indirect, 64 * 16),
        };
        gpu.Upload<uint>(ctx.StubBuffer, 0, [0, 0, 0, 0]);

        Textures.CreateBase(ctx);
        Materials.AddBuiltIn(ctx);
        PipelineClass forced = new(failureSurface, SurfaceVariant.DoubleSided | SurfaceVariant.FailureForced);
        ctx.Pipelines = new PipelineTable(Shaders.CompileBuiltIn(ctx, Pipelines.VertexTemplate, GpuStage.Vertex), forced, GBuffer.ColorFormats, gpu.DepthFormat);
        CompiledShader fullscreen = Shaders.CompileBuiltIn(ctx, "Passes/Fullscreen.vert.hlsl", GpuStage.Vertex);
        CompiledShader overlay = Shaders.CompileBuiltIn(ctx, "Passes/FailureOverlay.frag.hlsl", GpuStage.Fragment);
        ctx.Pipeline = new PipelineState(fullscreen, gpu.CreatePipeline(new PipelineDesc(fullscreen, overlay, FrameTargets.LdrFormat) { Cull = GpuCull.None }));

        // The built-in surfaces compile now, so the first frames of a scripted run draw them, and the failure pipelines are
        // ready before anything can fail.
        Pipelines.Prewarm(ctx, ctx.Materials.PlaceholderClass);
        if (failureSurface != 0)
        {
            Pipelines.Prewarm(ctx, forced);
            Pipelines.Prewarm(ctx, new PipelineClass(failureSurface, SurfaceVariant.None));
        }

        // The transparent surfaces' template and its includes are read now too, as the opaque one was by the prewarm, so
        // the first transparent material does not stall the render thread on them.
        Shaders.GetByPath(ctx, Pipelines.ForwardTemplate);

        if (gpu.Debug)
            RenderChecks.RunProbes(ctx);

        ctx.Building = false;
        return ctx;
    }

    /// <summary>
    /// Asset hot reload for the render state, before anything is recorded: a changed shader, material, texture or pass
    /// that is in use starts loading again off this thread, a shader with every shader that includes it. What is drawn
    /// stays as it was until that arrives (<see cref="ApplyReloads"/>).
    /// </summary>
    private static void ApplyAssetChanges(RenderContext ctx, ReadOnlySpan<Event> events)
    {
        HashSet<ulong>? changed = null;
        foreach (Event change in events)
        {
            if (change.Type is EventType.AssetAdded or EventType.AssetModified)
                (changed ??= []).Add(change.Id);
        }

        if (changed is null)
            return;

        foreach (ulong id in changed)
            Reload(ctx, id);
    }

    /// <summary>
    /// Starts loading one changed asset again, as whatever the renderer holds it as. Apart from
    /// <see cref="ApplyAssetChanges"/> so the lambdas' closure is made only for a change, not every frame.
    /// </summary>
    private static void Reload(RenderContext ctx, ulong id)
    {
        Preloads.Forget(ctx, id);
        if (ctx.Shaders.Entries.ContainsKey(id))
            ctx.Reloads.Start(id, cancel => Preloads.ShadersAsync(ctx.Assets, [.. Shaders.Affected(ctx, id)], cancel));
        else if (ctx.Pipeline.Passes.Contains(id))
            ctx.Reloads.Start(id, cancel => Preloads.PassAsync(ctx.Assets, id, cancel));
        else if (ctx.Pipeline.Current.IsValid && id == ctx.Pipeline.Current.Id)
            ctx.Reloads.Start(id, cancel => Preloads.PipelineAsync(ctx.Assets, id, cancel));
        else if (ctx.Materials.Contains(id))
            ctx.Reloads.Start(id, cancel => Preloads.MaterialAsync(ctx.Assets, new Handle<Material>(id), cancel));
        else if (ctx.Textures.Contains(id))
            ctx.Reloads.Start(id, cancel => Preloads.TextureAsync(ctx.Assets, id, cancel));

        if (ctx.Meshes.Contains(id))
            Debugging.Log.Warn($"Model {id} changed on disk; remove and re-add its entities to see the new geometry (live model reload arrives with streaming).");
    }

    /// <summary>
    /// The changed assets that have arrived are applied, each with what its load brought in hand. A shader drops
    /// everything built on it: its text and that of every shader including it is taken again, pipelines recompile,
    /// materials repack and a pass on it recompiles. A pass loads from its file as it is now and compiles, a material
    /// repacks, a texture is uploaded again. Records carry texture references resolved when they are written, and a
    /// texture that failed or was repaired moves its materials to or from the failure surface, so the materials
    /// repack after a texture; and when a material's class changed, every instance re-reads its class.
    /// </summary>
    private void ApplyReloads(RenderContext ctx, long frame)
    {
        _reloaded.Clear();
        ctx.Reloads.Poll((ctx, _reloaded, frame), static (state, id, job) => state._reloaded.Add((id, Preloads.Outcome(state.ctx, id, job, state.frame))));
        if (_reloaded.Count == 0)
            return;

        bool reclass = false, textures = false;
        foreach ((ulong id, Preloaded loaded) in _reloaded)
        {
            Preloads.Use(ctx, loaded);
            if (ctx.Shaders.Entries.ContainsKey(id))
            {
                HashSet<ulong> shaders = Shaders.Invalidate(ctx, id);

                // Taken again now, while their text is in hand: what recompiles later (every pipeline, once a
                // shared file's vertex shader is in) finds them there.
                foreach (ulong shader in shaders)
                    Shaders.Get(ctx, new Handle<Shader>(shader));

                Pipelines.Invalidate(ctx, shaders);
                reclass |= Materials.RepackShaders(ctx, shaders);
                foreach ((ulong pass, PassState state) in ctx.Pipeline.Passes.Entries.ToArray())
                {
                    if (shaders.Contains(state.ShaderId) || (state.FragmentId != 0 && shaders.Contains(state.FragmentId)))
                        PassLoader.Compile(ctx, pass);
                }

                Debugging.Log.Info($"Shaders changed: {shaders.Count} shader(s) rebuild.");
            }

            if (ctx.Pipeline.Passes.TryGet(id, out PassState listed))
            {
                bool first = listed.Path.Length == 0; // just listed: this is its first load, not a reload
                PassLoader.Load(ctx, id);
                if (!first)
                    Debugging.Log.Info($"Pass {listed.Path} reloaded{(listed.Error is null ? "" : " (disabled)")}.");
            }

            if (ctx.Pipeline.Current.IsValid && id == ctx.Pipeline.Current.Id)
                PipelineSync.Loaded(ctx);

            if (ctx.Materials.Contains(id))
            {
                reclass |= Materials.Reload(ctx, id);
                Debugging.Log.Info($"Material {id} reloaded.");
            }

            if (ctx.Textures.Contains(id))
            {
                Textures.Reload(ctx, id);
                textures = true;
                Debugging.Log.Info($"Texture {id} uploaded again.");
            }

            Preloads.Done(ctx);
        }

        if (textures)
            reclass |= Materials.RepackTextures(ctx);

        if (reclass)
            Instancing.Reclass(ctx);
    }

    private void OnRendererSet(Handle entity)
    {
        // The first Set has no instance yet and is picked up by the registration pass; a later one registers again.
        if (_ecs.Has<RenderInstance>(entity))
            _changed.Add(entity);
    }

    private void OnRendererRemoved(Handle entity)
    {
        if (_ecs.TryGet<RenderInstance>(entity, out RenderInstance instance))
            _removed.Add((entity, instance));
    }

    /// <summary>
    /// The renderers the observers saw removed leave the render state.
    /// </summary>
    private void Unregister(RenderContext ctx)
    {
        foreach ((Handle entity, RenderInstance instance) in _removed)
        {
            Instancing.Remove(ctx, instance.Handle);
            if (_ecs.IsAlive(entity) && _ecs.Has<RenderInstance>(entity))
                _ecs.Remove<RenderInstance>(entity);
        }

        _removed.Clear();
    }

    /// <summary>
    /// The entities the observers saw hidden or shown: every instance under one is hidden when it or anything above
    /// it is, and drawn otherwise. One shown as another is destroyed changes places with it in this frame.
    /// </summary>
    private void ApplyHidden(RenderContext ctx)
    {
        _lastParent = Handle.None;
        _lastParentHidden = false;
        foreach (Handle entity in _hiddenChanged)
        {
            if (_ecs.IsAlive(entity))
                Hide(ctx, entity, IsHidden(_ecs.GetParent(entity)));
        }

        _hiddenChanged.Clear();
    }

    private void Hide(RenderContext ctx, Handle entity, bool above)
    {
        bool hidden = above || _ecs.Has<Hidden>(entity);
        if (_ecs.TryGet<RenderInstance>(entity, out RenderInstance instance))
            Instancing.Hide(ctx, instance.Handle, hidden);

        foreach (Handle child in _ecs.GetChildren(entity))
            Hide(ctx, child, hidden);
    }

    /// <summary>
    /// Whether the entity, or anything above it, is <see cref="Hidden"/>.
    /// </summary>
    private bool IsHidden(Handle entity)
    {
        for (; entity.IsValid; entity = _ecs.GetParent(entity))
        {
            if (_ecs.Has<Hidden>(entity))
                return true;
        }

        return false;
    }

    /// <summary>
    /// <see cref="IsHidden"/> for one of many entities asked about in a row: what is above its parent is looked up
    /// once for all that share it.
    /// </summary>
    private bool IsHiddenUnderLastParent(Handle entity)
    {
        Handle parent = _ecs.GetParent(entity);
        if (parent != _lastParent)
        {
            _lastParent = parent;
            _lastParentHidden = IsHidden(parent);
        }

        return _lastParentHidden || _ecs.Has<Hidden>(entity);
    }

    /// <summary>
    /// The renderers the observers saw set again are registered again, their <see cref="RenderInstance"/> taking the new
    /// handle in place. One whose new model or materials have yet to arrive loses its instance and registers when they do.
    /// </summary>
    private void Reregister(RenderContext ctx)
    {
        foreach (Handle entity in _changed)
        {
            if (!_ecs.IsAlive(entity) || !_ecs.TryGet<RenderInstance>(entity, out RenderInstance instance))
                continue;

            Instancing.Remove(ctx, instance.Handle);
            if (_ecs.TryGet<Renderer>(entity, out Renderer renderer) && _ecs.TryGet<WorldTransform>(entity, out WorldTransform world) && Preloads.Ready(ctx, renderer))
                Add(ctx, entity, renderer, world.Value);
            else
                _ecs.Remove<RenderInstance>(entity);
        }

        _changed.Clear();
    }

    private void Register(RenderContext ctx)
    {
        if (_unregistered.Count() == 0)
            return;

        // Collected first: giving an entity its RenderInstance moves it out of the chunk being read.
        _toRegister.Clear();
        _unregistered.Run(_collectUnregistered);
        long started = Stopwatch.GetTimestamp();
        List<(uint FirstVertex, uint FirstIndex, Vertex[] Vertices, uint[] Indices)> uploads = ctx.Meshes.Pending;
        int counted = uploads.Count, added = 0;
        long geometry = 0;
        foreach ((Handle entity, Renderer renderer, Matrix4x4 world) in _toRegister)
        {
            // One whose assets are still loading is looked at again next frame; until then it is not drawn.
            if (!Preloads.Ready(ctx, renderer))
                continue;

            Add(ctx, entity, renderer, world);
            for (; counted < uploads.Count; counted++)
                geometry += ((long)uploads[counted].Vertices.Length * Vertex.Size) + ((long)uploads[counted].Indices.Length * sizeof(uint));

            // The rest next frame: this one has done its share.
            if (geometry >= GeometryBudgetBytes || (++added % 32 == 0 && Stopwatch.GetElapsedTime(started).TotalMilliseconds >= RegisterBudgetMs))
                break;
        }
    }

    private void Add(RenderContext ctx, Handle entity, in Renderer renderer, in Matrix4x4 world)
    {
        bool isStatic = _ecs.Has<Static>(entity);
        bool hidden = IsHiddenUnderLastParent(entity);
        Preloads.Use(ctx, renderer);
        uint handle = Instancing.Add(ctx, new InstanceDesc(renderer.Model, renderer.Materials, renderer.Flags, renderer.CullRadius, world, isStatic, hidden));
        Preloads.Done(ctx);
        _ecs.Set(entity, new RenderInstance { Handle = handle, Static = isStatic });
    }

    private void CollectUnregistered(ReadOnlySpan<Handle> entities, Span<Renderer> renderers, Span<WorldTransform> worlds)
    {
        int count = Math.Min(entities.Length, RegisterCandidates - _toRegister.Count);
        for (int i = 0; i < count; i++)
            _toRegister.Add((entities[i], renderers[i], worlds[i].Value));
    }

    private void Move(ReadOnlySpan<Handle> entities, Span<WorldTransform> worlds, Span<RenderInstance> instances)
    {
        if (Context is { } ctx)
            Instancing.Move(ctx, instances, worlds);
    }

    private void CollectViews(ReadOnlySpan<Handle> entities, Span<Camera> cameras, Span<WorldTransform> worlds)
    {
        for (int i = 0; i < cameras.Length; i++)
        {
            // A hidden camera draws nothing: that of a domain still loading behind the loading domain, say.
            if (IsHiddenUnderLastParent(entities[i]))
                continue;

            _views.Add(new View(cameras[i], worlds[i].Value));
        }
    }

    /// <summary>
    /// The world's one <see cref="PostProcessing"/>; of several the first is followed, warned about once while it lasts.
    /// </summary>
    private void CollectPostProcessing()
    {
        _posts = default;
        _postLists = 0;
        _postProcessing.Run(_collectPosts);

        if (_postLists > 1 && !_warnedPostLists)
            Debugging.Log.Warn($"{_postLists} entities carry a PostProcessing; post-processing is global, so only the first is followed.");

        _warnedPostLists = _postLists > 1;
    }

    private void CollectPosts(ReadOnlySpan<Handle> entities, Span<PostProcessing> postProcessing)
    {
        if (_postLists == 0 && postProcessing.Length > 0)
            _posts = postProcessing[0].Posts;

        _postLists += postProcessing.Length;
    }

    /// <summary>
    /// The world's one <see cref="Sky"/>; of several the first is followed, warned about once; without one the default.
    /// </summary>
    private void CollectSkies()
    {
        _sky = null;
        _skyCount = 0;
        _skies.Run(_collectSky);

        if (_skyCount > 1 && !_warnedSkies)
            Debugging.Log.Warn($"{_skyCount} entities carry a Sky; the sky is global, so only the first is followed.");

        _warnedSkies = _skyCount > 1;
    }

    private void CollectSky(ReadOnlySpan<Handle> entities, Span<Sky> skies)
    {
        if (_skyCount == 0 && skies.Length > 0)
            _sky = skies[0].Color.Rgb;

        _skyCount += skies.Length;
    }

    /// <summary>
    /// The lights and glows that never move, read again when one of them changed (the observers and the hidden
    /// changes say when): most of a big scene's lights are these, and a frame then only reads the ones that can move.
    /// </summary>
    private void CollectSettled()
    {
        if (!_settledChanged)
            return;

        _settledChanged = false;
        _settledLights.Clear();
        _settledGlows.Clear();
        (_collectedLights, _collectedGlows) = (_settledLights, _settledGlows);
        _directionalSettled.Run(_collectDirectional);
        _pointsSettled.Run(_collectPoints);
        _spotsSettled.Run(_collectSpots);
        _areasSettled.Run(_collectAreas);
        _glowingSettled.Run(_collectGlows);
        (_collectedLights, _collectedGlows) = (_lights, _glows);
    }

    /// <summary>
    /// <see cref="IsHidden"/> for all the entities a query hands over at once, asked of the first: they are rows of
    /// one table, and an entity's parent and whether it carries <see cref="Hidden"/> are part of what table it is in,
    /// so they share both. The queries do not look up the hierarchy themselves: a query that does is re-matched over
    /// every table by the ECS each time a chunk's root is hidden or shown, a stall of many milliseconds while streaming.
    /// </summary>
    private bool IsHiddenTogether(ReadOnlySpan<Handle> entities)
    {
        return entities.Length > 0 && IsHiddenUnderLastParent(entities[0]);
    }

    /// <summary>
    /// The lights of one kind a query hands over, as <see cref="LightInstance"/>s, unless they are hidden.
    /// </summary>
    private void CollectLights<T>(ReadOnlySpan<Handle> entities, Span<T> lights, Span<WorldTransform> worlds, LightOf<T> of)
    {
        if (IsHiddenTogether(entities))
            return;

        for (int i = 0; i < lights.Length; i++)
            _collectedLights.Add(of(lights[i], worlds[i].Value));
    }

    private void CollectGlows(ReadOnlySpan<Handle> entities, Span<Glow> glows, Span<WorldTransform> worlds)
    {
        if (IsHiddenTogether(entities))
            return;

        for (int i = 0; i < glows.Length; i++)
            _collectedGlows.Add(new GpuGlow { PositionRadius = new Vector4(worlds[i].Value.Translation, glows[i].Radius), Color = new Vector4(glows[i].Color.Rgb, 1f) });
    }

    /// <summary>
    /// Drops every <see cref="RenderInstance"/>: they index a render state that is gone.
    /// </summary>
    private void StripInstances()
    {
        _removed.Clear();
        _changed.Clear();
        _hiddenChanged.Clear();

        if (_allInstances.Count() == 0)
            return;

        _toStrip.Clear();
        _allInstances.Run((ReadOnlySpan<Handle> entities, Span<RenderInstance> _) => _toStrip.AddRange(entities));
        foreach (Handle entity in _toStrip)
            _ecs.Remove<RenderInstance>(entity);

        Debugging.Log.Info($"Rendering changed: {_toStrip.Count} instance(s) will register again.");
    }
}
