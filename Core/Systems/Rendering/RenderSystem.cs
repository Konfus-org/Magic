using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Contexts.Events;
using Magic.Contexts.Rendering;
using Magic.Contexts.Settings;
using Magic.Interfaces;
using Magic.Services;
using Magic.Utils;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Magic.Systems.Rendering;

/// <summary>
/// Draws the entities through whatever <see cref="IRendering"/> is loaded, which is only the GPU: everything else (the GPU
/// tables, shaders and pipelines, culling, passes, hot reload) is here and in <c>Systems/Rendering</c>, its state in one
/// <see cref="RenderContext"/>. <see cref="Run"/> runs in Render, after LateUpdate and the transforms, one way, top to bottom:
/// asset changes reload; the entities are synced into the tables (every <see cref="Renderer"/> that has a
/// <see cref="WorldTransform"/> and no <see cref="RenderInstance"/> yet is registered, the ones whose
/// <see cref="Renderer"/> went away are forgotten and the ones set again are registered again, both told by observers so
/// nothing is swept, and the non-static ones give their world matrices); what changed is uploaded; the frame is planned;
/// and the plan is recorded into <see cref="Frame.DrawCommands"/>. Then the gems loaded after the ECS may add their own
/// commands on top in their Render hook and the host submits the list, leaving how long that took on it for the next
/// frame's <see cref="Stats"/>. The renderer gem is static, so the one given here is kept; without one nothing is drawn.
/// </summary>
internal sealed class RenderSystem : ISystem
{
    private readonly IEcs _ecs;
    private readonly Assets _assets;
    private readonly IFileSystem _files;
    private readonly Project _project;
    private readonly IWindowRegistry? _windows;
    private readonly IRendering? _rendering;
    private readonly IEcsQuery<Renderer, WorldTransform> _unregistered;
    private readonly IEcsQuery<WorldTransform, RenderInstance> _movable;
    private readonly IEcsQuery<RenderInstance> _allInstances;
    private readonly IEcsQuery<Camera, WorldTransform> _cameras;
    private readonly IEcsQuery<PostProcessing> _postProcessing;
    private readonly IEcsQuery<DirectionalLight, WorldTransform> _directional;
    private readonly IEcsQuery<PointLight, WorldTransform> _points;
    private readonly IEcsQuery<SpotLight, WorldTransform> _spots;
    private readonly IDisposable _onSet;
    private readonly IDisposable _onRemoved;

    // Queued by the observers (which must not change the world), applied at the start of the next pass.
    private readonly List<(Handle Entity, RenderInstance Instance)> _removed = [];
    private readonly List<Handle> _changed = [];

    private readonly List<(Handle Entity, Renderer Renderer, Matrix4x4 World)> _toRegister = [];
    private readonly List<View> _views = [];
    private readonly List<LightInstance> _lights = [];
    private readonly List<Handle> _toStrip = [];
    private readonly FramePlan _plan = new();

    // One delegate each, not one per frame.
    private readonly QueryChunkAction<Renderer, WorldTransform> _collectUnregistered;
    private readonly QueryChunkAction<WorldTransform, RenderInstance> _move;
    private readonly QueryChunkAction<Camera, WorldTransform> _collectViews;
    private readonly QueryChunkAction<PostProcessing> _collectPasses;
    private readonly QueryChunkAction<DirectionalLight, WorldTransform> _collectDirectional;
    private readonly QueryChunkAction<PointLight, WorldTransform> _collectPoints;
    private readonly QueryChunkAction<SpotLight, WorldTransform> _collectSpots;

    private BuiltWith? _builtWith; // null until the first run
    private PassList _passes;      // the world's post-processing this frame; empty without one
    private int _passLists;        // how many entities carry a PostProcessing this frame
    private bool _warnedPassLists;

    public RenderSystem(IEcs ecs, Assets assets, IFileSystem files, Project project, IWindowRegistry? windows, IRendering? rendering)
    {
        _ecs = ecs;
        _assets = assets;
        _files = files;
        _project = project;
        _windows = windows;
        _rendering = rendering;
        _unregistered = ecs.Query<Renderer, WorldTransform>().Without<RenderInstance>().Build();
        _movable = ecs.Query<WorldTransform, RenderInstance>().Build();
        _allInstances = ecs.Query<RenderInstance>().Build();
        _cameras = ecs.Query<Camera, WorldTransform>().Build();
        _postProcessing = ecs.Query<PostProcessing>().Build();
        _directional = ecs.Query<DirectionalLight, WorldTransform>().Build();
        _points = ecs.Query<PointLight, WorldTransform>().Build();
        _spots = ecs.Query<SpotLight, WorldTransform>().Build();
        _onSet = ecs.Observe<Renderer>(ComponentEvent.Set, OnRendererSet);
        _onRemoved = ecs.Observe<Renderer>(ComponentEvent.Removed, OnRendererRemoved);
        _collectUnregistered = CollectUnregistered;
        _move = Move;
        _collectViews = CollectViews;
        _collectPasses = CollectPasses;
        _collectDirectional = CollectDirectional;
        _collectPoints = CollectPoints;
        _collectSpots = CollectSpots;
    }

    public void Dispose()
    {
        _onSet.Dispose();
        _onRemoved.Dispose();
        _unregistered.Dispose();
        _movable.Dispose();
        _allInstances.Dispose();
        _cameras.Dispose();
        _postProcessing.Dispose();
        _directional.Dispose();
        _points.Dispose();
        _spots.Dispose();
    }

    public UpdateType Phase => UpdateType.Render;

    /// <summary>What the last frame did; default when there is no renderer.</summary>
    public RenderStats Stats { get; private set; }

    /// <summary>Milliseconds the last frame spent syncing the entities in, and recording plus the submit before it.</summary>
    public float SyncMs { get; private set; }

    public float RenderMs { get; private set; }

    /// <summary>The render state of the current renderer; null without one.</summary>
    private RenderContext? Context { get; set; }

    /// <summary>
    /// Syncs the entities into the render state and records the scene into <paramref name="frame"/>'s commands, drawing into
    /// the windows of the <see cref="IWindowRegistry"/>. Without a renderer it does nothing.
    /// </summary>
    public void Run(in Frame frame)
    {
        long started = Stopwatch.GetTimestamp();
        if (_builtWith != BuiltWith.From(_project.Settings.Render))
            RebuildContext();

        if (Context is not { } ctx)
            return;

        // The previous frame was submitted and nothing of this one is uploaded yet: in a debugging renderer, the periodic
        // culling check of what it drew.
        if (ctx.Gpu.Debug && frame.Number % RenderChecks.VerifyEveryFrames == 0 && _plan.Views.Count > 0)
            RenderChecks.Verify(ctx, _plan.Views[0].Buffers, _plan.Views[0].Constants);

        // Reload: what changed on disk, and the compiles that finished.
        ApplyAssetChanges(ctx, frame.Events.Span);
        ctx.Pipelines.Compiles.Poll(ctx, Pipelines.FinishCompile);
        ctx.Passes.Compiles.Poll(ctx, Passes.FinishCompile);

        // Sync: the entities into the tables. The moves read every chunk's columns as they are: no copy, and statics and
        // unchanged matrices are skipped.
        Unregister(ctx);
        Reregister(ctx);
        Register(ctx);

        _movable.Run(_move);
        _views.Clear();
        _cameras.Run(_collectViews);
        CollectPostProcessing();
        _lights.Clear();
        _directional.Run(_collectDirectional);
        _points.Run(_collectPoints);
        _spots.Run(_collectSpots);

        // Upload: what the tables changed.
        Textures.Flush(ctx);
        Materials.Flush(ctx);
        Instancing.Flush(ctx);

        long recording = Stopwatch.GetTimestamp();
        SyncMs = (float)Stopwatch.GetElapsedTime(started, recording).TotalMilliseconds;

        // Plan, then record: the plan is everything the commands are made from.
        Scene.Plan(ctx, _plan, _windows, CollectionsMarshal.AsSpan(_views), _passes, LightingConstants.From(CollectionsMarshal.AsSpan(_lights)), (float)frame.Time);
        (int draws, int dispatches) = Scene.Record(ctx, frame.DrawCommands, _plan);
        float recordMs = (float)Stopwatch.GetElapsedTime(recording).TotalMilliseconds;

        // The host submits after this, so the submit and the wait in the stats are the previous frame's.
        float waitMs = frame.DrawCommands.WaitMs;
        RenderMs = recordMs + frame.DrawCommands.SubmitMs;
        Stats = new RenderStats(
            ctx.Instances.Alive, (uint)draws, (uint)dispatches, (uint)ctx.Pipelines.Pending,
            (uint)ctx.Meshes.Count, (uint)ctx.Textures.Resident, SyncMs, RenderMs - waitMs, waitMs);
    }

    /// <summary>
    /// The first run, or render settings a context is built from changed: every instance registered with the old
    /// context is stale, and the old context goes. A renderer whose built-in shaders do not compile draws nothing,
    /// logged once.
    /// </summary>
    private void RebuildContext()
    {
        if (_builtWith is not null)
            Debugging.Log.Info("Render settings changed: the render state is built again.");

        StripInstances();
        Context = null;
        Stats = default;
        _plan.Clear();
        if (_rendering is not null)
        {
            try
            {
                Context = CreateContext(_rendering, _assets, _files, _project);
            }
            catch (InvalidOperationException ex)
            {
                Debugging.Log.Error($"The renderer cannot draw the scene: {ex.Message}");
            }
        }

        _builtWith = BuiltWith.From(_project.Settings.Render);
    }

    /// <summary>
    /// A context for <paramref name="gpu"/>: the tables, the built-in shaders and the prewarmed failure pipelines. Throws
    /// when a built-in shader does not compile: nothing could be drawn.
    /// </summary>
    private static RenderContext CreateContext(IRendering gpu, Assets assets, IFileSystem files, Project project)
    {
        GpuStructs.AssertLayout();
        RenderSettings settings = project.Settings.Render;
        Debugging.Log.Verbose($"Render settings: occlusion {settings.OcclusionCulling}, anisotropy {settings.Anisotropy}, resolution {(settings.Resolution.IsEmpty ? "the window's" : $"{settings.Resolution.Width} x {settings.Resolution.Height}")}.");

        ulong defaultSurface = assets.Find<Shader>("Shaders/Surfaces/Pbr.surf.hlsl").Id;
        ulong failureSurface = assets.Find<Shader>("Shaders/Surfaces/Failure.surf.hlsl").Id;
        if (defaultSurface == 0)
            Debugging.Log.Error("Shaders/Surfaces/Pbr.surf.hlsl is not an indexed asset: there is no default material.");
        if (failureSurface == 0)
            Debugging.Log.Error("Shaders/Surfaces/Failure.surf.hlsl is not an indexed asset: broken materials and shaders will draw grey or not at all.");

        string? cache = settings.ShaderCache ? Path.Combine(Project.Cache, "Shaders", gpu.ShaderFormat) : null;
        RenderContext ctx = new()
        {
            Gpu = gpu,
            Assets = assets,
            Files = files,
            Project = project,
            Settings = settings,
            Shaders = new ShaderCache(Path.Combine(project.Resources, "Shaders"), cache, gpu.ShaderFormat),
            Textures = new TextureTable(gpu, settings.Anisotropy),
            Meshes = new MeshTable(gpu, 1 << 20, 1 << 21),
            Materials = new MaterialTable(gpu, defaultSurface, failureSurface),
            Instances = new InstanceTable(gpu, 4096),
            Buckets = new Buckets(gpu, 1 << 22),
            LinearClamp = gpu.CreateSampler(new SamplerDesc(GpuFilter.Linear, GpuAddress.Clamp)),
            NearestClamp = gpu.CreateSampler(new SamplerDesc(GpuFilter.Nearest, GpuAddress.Clamp)),
        };

        Textures.CreateBase(ctx);
        Materials.AddBuiltIn(ctx);
        PipelineClass forced = new(failureSurface, SurfaceVariant.DoubleSided | SurfaceVariant.FailureForced);
        ctx.Pipelines = new PipelineTable(Shaders.CompileBuiltIn(ctx, Pipelines.VertexTemplate, GpuStage.Vertex), forced, FrameTargets.HdrFormat, gpu.DepthFormat);
        ctx.Cull = Culling.Create(ctx);
        ctx.Passes = new PassTable(Shaders.CompileBuiltIn(ctx, "Passes/Fullscreen.vert.hlsl", GpuStage.Vertex));

        // The built-in surfaces compile now, so the first frames of a scripted run draw them, and the failure pipelines are
        // ready before anything can fail.
        Pipelines.Prewarm(ctx, ctx.Materials.PlaceholderClass);
        if (failureSurface != 0)
        {
            Pipelines.Prewarm(ctx, forced);
            Pipelines.Prewarm(ctx, new PipelineClass(failureSurface, SurfaceVariant.None));
        }

        if (gpu.Debug)
            RenderChecks.RunProbes(ctx);

        return ctx;
    }

    /// <summary>
    /// Asset hot reload for the render state, before anything is recorded. Changed shaders drop everything built on them:
    /// pipelines recompile and materials repack; materials and textures reload; the loaded passes follow their files and
    /// shaders. When a material's class changed, every instance re-reads its class.
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

        HashSet<ulong> shaders = [];
        foreach (ulong id in changed)
        {
            if (ctx.Shaders.Entries.ContainsKey(id))
                shaders.UnionWith(Shaders.Invalidate(ctx, id));
        }

        bool reclass = false;
        if (shaders.Count > 0)
        {
            Pipelines.Invalidate(ctx, shaders);
            reclass |= Materials.RepackShaders(ctx, shaders);
            Debugging.Log.Info($"Shaders changed: {shaders.Count} shader(s) rebuild.");
        }

        // A changed .pass reloads; a pass whose shader (or an include of it) changed recompiles.
        List<PassState> passes = ctx.Passes.States;
        for (int i = 0; i < passes.Count; i++)
        {
            PassState pass = passes[i];
            if (changed.Contains(pass.Id))
            {
                Passes.Load(ctx, pass.Id);
                Debugging.Log.Info($"Pass {pass.Path} reloaded{(passes[i].Error is null ? "" : " (disabled)")}.");
            }
            else if (shaders.Contains(pass.ShaderId))
                Passes.Compile(ctx, i);
        }

        bool textures = false;
        foreach (ulong id in changed)
        {
            if (ctx.Materials.Owns(id))
            {
                reclass |= Materials.Reload(ctx, id);
                Debugging.Log.Info($"Material {id} reloaded.");
            }

            if (ctx.Textures.Owns(id))
            {
                Textures.Reload(ctx, id);
                textures = true;
                Debugging.Log.Info($"Texture {id} uploaded again.");
            }

            if (ctx.Meshes.Owns(id))
                Debugging.Log.Warn($"Model {id} changed on disk; remove and re-add its entities to see the new geometry (live model reload arrives with streaming).");
        }

        // Records carry texture references resolved when they are written, and a texture that failed or was repaired
        // moves its materials to or from the failure surface.
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

    /// <summary>The renderers the observers saw removed leave the render state.</summary>
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

    /// <summary>The renderers the observers saw set again are registered again, their <see cref="RenderInstance"/> taking the new handle in place.</summary>
    private void Reregister(RenderContext ctx)
    {
        foreach (Handle entity in _changed)
        {
            if (!_ecs.IsAlive(entity) || !_ecs.TryGet<RenderInstance>(entity, out RenderInstance instance))
                continue;

            Instancing.Remove(ctx, instance.Handle);
            if (_ecs.TryGet<Renderer>(entity, out Renderer renderer) && _ecs.TryGet<WorldTransform>(entity, out WorldTransform world))
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
        foreach ((Handle entity, Renderer renderer, Matrix4x4 world) in _toRegister)
            Add(ctx, entity, renderer, world);
    }

    private void Add(RenderContext ctx, Handle entity, in Renderer renderer, in Matrix4x4 world)
    {
        bool isStatic = _ecs.TryGet<Tags>(entity, out Tags tags) && tags.Has(Tag.Static);
        uint handle = Instancing.Add(ctx, new InstanceDesc(renderer.Model, renderer.Materials, renderer.Flags, world, isStatic));
        _ecs.Set(entity, new RenderInstance { Handle = handle, Static = isStatic });
    }

    private void CollectUnregistered(ReadOnlySpan<Handle> entities, Span<Renderer> renderers, Span<WorldTransform> worlds)
    {
        for (int i = 0; i < entities.Length; i++)
            _toRegister.Add((entities[i], renderers[i], worlds[i].Value));
    }

    private void Move(ReadOnlySpan<Handle> entities, Span<WorldTransform> worlds, Span<RenderInstance> instances)
    {
        Instancing.Move(Context!, instances, worlds);
    }

    private void CollectViews(ReadOnlySpan<Handle> entities, Span<Camera> cameras, Span<WorldTransform> worlds)
    {
        for (int i = 0; i < cameras.Length; i++)
            _views.Add(new View(cameras[i], worlds[i].Value));
    }

    /// <summary>The world's one <see cref="PostProcessing"/>; of several the first is followed, warned about once while it lasts.</summary>
    private void CollectPostProcessing()
    {
        _passes = default;
        _passLists = 0;
        _postProcessing.Run(_collectPasses);

        if (_passLists > 1 && !_warnedPassLists)
            Debugging.Log.Warn($"{_passLists} entities carry a PostProcessing; post-processing is global, so only the first is followed.");

        _warnedPassLists = _passLists > 1;
    }

    private void CollectPasses(ReadOnlySpan<Handle> entities, Span<PostProcessing> postProcessing)
    {
        if (_passLists == 0 && postProcessing.Length > 0)
            _passes = postProcessing[0].Passes;

        _passLists += postProcessing.Length;
    }

    private void CollectDirectional(ReadOnlySpan<Handle> entities, Span<DirectionalLight> lights, Span<WorldTransform> worlds)
    {
        for (int i = 0; i < lights.Length; i++)
            _lights.Add(new LightInstance(LightKind.Directional, lights[i].Color, lights[i].Intensity, 0f, 0f, 0f, lights[i].CastsShadows, worlds[i].Value));
    }

    private void CollectPoints(ReadOnlySpan<Handle> entities, Span<PointLight> lights, Span<WorldTransform> worlds)
    {
        for (int i = 0; i < lights.Length; i++)
            _lights.Add(new LightInstance(LightKind.Point, lights[i].Color, lights[i].Intensity, lights[i].Range, 0f, 0f, lights[i].CastsShadows, worlds[i].Value));
    }

    private void CollectSpots(ReadOnlySpan<Handle> entities, Span<SpotLight> lights, Span<WorldTransform> worlds)
    {
        for (int i = 0; i < lights.Length; i++)
            _lights.Add(new LightInstance(LightKind.Spot, lights[i].Color, lights[i].Intensity, lights[i].Range, lights[i].InnerAngle, lights[i].OuterAngle, lights[i].CastsShadows, worlds[i].Value));
    }

    /// <summary>Drops every <see cref="RenderInstance"/>: they index a render state that is gone.</summary>
    private void StripInstances()
    {
        _removed.Clear();
        _changed.Clear();

        if (_allInstances.Count() == 0)
            return;

        _toStrip.Clear();
        _allInstances.Run((ReadOnlySpan<Handle> entities, Span<RenderInstance> _) => _toStrip.AddRange(entities));
        foreach (Handle entity in _toStrip)
            _ecs.Remove<RenderInstance>(entity);

        Debugging.Log.Info($"Rendering changed: {_toStrip.Count} instance(s) will register again.");
    }

    /// <summary>The render settings a <see cref="RenderContext"/> is built from: when one of them changes, it is built again.</summary>
    private readonly record struct BuiltWith(bool OcclusionCulling, float Anisotropy, bool ShaderCache)
    {
        public static BuiltWith From(RenderSettings settings)
        {
            return new BuiltWith(settings.OcclusionCulling, settings.Anisotropy, settings.ShaderCache);
        }
    }
}
