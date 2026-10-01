using Magic.Contexts;
using Magic.Contexts.Components;
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
/// <see cref="RenderContext"/>. <see cref="Record"/> runs after LateUpdate and the transforms, one way, top to bottom:
/// asset changes reload; the entities are synced into the tables (every <see cref="Renderer"/> that has a
/// <see cref="WorldTransform"/> and no <see cref="RenderInstance"/> yet is registered, the ones whose
/// <see cref="Renderer"/> went away are forgotten and the ones set again are registered again, both told by observers so
/// nothing is swept, and the non-static ones give their world matrices); what changed is uploaded; the frame is planned;
/// and the plan is recorded into <see cref="Frame.Commands"/>. Then every gem's Render hook may add its own commands on
/// top, the host submits the list, and <see cref="Finish"/> takes the timings. The renderer is handed in every frame
/// because it lives in a reloadable gem: a different one gets a new context, so every <see cref="RenderInstance"/> is
/// stale and is dropped to be registered again.
/// </summary>
public sealed class RenderSystem : IDisposable
{
    private readonly IEcs _ecs;
    private readonly Assets _assets;
    private readonly IFileSystem _files;
    private readonly Project _project;
    private readonly IEcsQuery<Renderer, WorldTransform> _unregistered;
    private readonly IEcsQuery<WorldTransform, RenderInstance> _instances;
    private readonly IEcsQuery<RenderInstance> _registered;
    private readonly IEcsQuery<Camera, WorldTransform> _cameras;
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
    private readonly List<Handle> _scratch = [];
    private readonly FramePlan _plan = new();

    // One delegate each, not one per frame.
    private readonly QueryChunkAction<Renderer, WorldTransform> _collectUnregistered;
    private readonly QueryChunkAction<WorldTransform, RenderInstance> _move;
    private readonly QueryChunkAction<Camera, WorldTransform> _collectViews;
    private readonly QueryChunkAction<DirectionalLight, WorldTransform> _collectDirectional;
    private readonly QueryChunkAction<PointLight, WorldTransform> _collectPoints;
    private readonly QueryChunkAction<SpotLight, WorldTransform> _collectSpots;

    private IRendering? _renderer;
    private float _recordMs;
    private (int Draws, int Dispatches) _recorded;
    private BuiltWith _builtWith;

    public RenderSystem(IEcs ecs, Assets assets, IFileSystem files, Project project)
    {
        _ecs = ecs;
        _assets = assets;
        _files = files;
        _project = project;
        _unregistered = ecs.Query<Renderer, WorldTransform>().Without<RenderInstance>().Build();
        _instances = ecs.Query<WorldTransform, RenderInstance>().Build();
        _registered = ecs.Query<RenderInstance>().Build();
        _cameras = ecs.Query<Camera, WorldTransform>().Build();
        _directional = ecs.Query<DirectionalLight, WorldTransform>().Build();
        _points = ecs.Query<PointLight, WorldTransform>().Build();
        _spots = ecs.Query<SpotLight, WorldTransform>().Build();
        _onSet = ecs.Observe<Renderer>(ComponentEvent.Set, OnRendererSet);
        _onRemoved = ecs.Observe<Renderer>(ComponentEvent.Removed, OnRendererRemoved);
        _collectUnregistered = CollectUnregistered;
        _move = Move;
        _collectViews = CollectViews;
        _collectDirectional = CollectDirectional;
        _collectPoints = CollectPoints;
        _collectSpots = CollectSpots;
    }

    /// <summary>What the last frame did; default when there is no renderer.</summary>
    public RenderStats Stats { get; private set; }

    /// <summary>Milliseconds the last frame spent syncing the entities in, and recording and submitting.</summary>
    public float SyncMs { get; private set; }

    public float RenderMs { get; private set; }

    /// <summary>The render state of the current renderer, for the tests; null without one.</summary>
    internal RenderContext? Context { get; private set; }

    /// <summary>
    /// Syncs the entities into the render state and records the scene into <paramref name="frame"/>'s commands, drawing into
    /// the windows of <paramref name="windows"/>. None loaded does nothing.
    /// </summary>
    public void Record(in Frame frame, IRendering? rendering, IWindowRegistry? windows)
    {
        long started = Stopwatch.GetTimestamp();
        if (!ReferenceEquals(rendering, _renderer) || _builtWith != BuiltWith.From(_project.Settings.Render))
            Reset(rendering);

        if (Context is not { } ctx)
            return;

        // Reload: what changed on disk, and the compiles that finished.
        AssetChanges.Apply(ctx, frame.Events.Span);
        Pipelines.Update(ctx);
        Passes.Update(ctx);

        // Sync: the entities into the tables. The moves read every chunk's columns as they are: no copy, and statics and
        // unchanged matrices are skipped.
        Unregister(ctx);
        Reregister(ctx);
        Register(ctx);
        _instances.Run(_move);

        _views.Clear();
        _cameras.Run(_collectViews);
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
        Scene.Plan(ctx, _plan, windows, CollectionsMarshal.AsSpan(_views), LightingConstants.From(CollectionsMarshal.AsSpan(_lights)), (float)frame.Time);
        _recorded = Scene.Record(ctx, frame.Commands, _plan);
        _recordMs = (float)Stopwatch.GetElapsedTime(recording).TotalMilliseconds;
    }

    /// <summary>
    /// The frame's commands were submitted, which took <paramref name="submitMs"/>, <paramref name="waitMs"/> of it
    /// blocked on the GPU: the stats of the frame, and in a debugging renderer the periodic culling check.
    /// </summary>
    public void Finish(in Frame frame, float submitMs, float waitMs)
    {
        if (Context is not { } ctx)
            return;

        RenderMs = _recordMs + submitMs;
        Stats = new RenderStats(
            ctx.Instances.Alive, (uint)_recorded.Draws, (uint)_recorded.Dispatches, (uint)ctx.Pipelines.Pending,
            (uint)ctx.Meshes.Count, (uint)ctx.Textures.Resident, SyncMs, RenderMs - waitMs, waitMs);

        if (ctx.Gpu.Debug && frame.Number % RenderChecks.VerifyEveryFrames == 0 && _plan.Views.Count > 0)
            RenderChecks.Verify(ctx, _plan.Views[0].Buffers, _plan.Views[0].Constants);
    }

    /// <summary>What the target's last frame looked like, before anything the gems drew over it; failed, with why, when there is none.</summary>
    public Result<CapturedFrame> Capture(RenderTarget target)
    {
        if (Context is null || !Context.Targets.TryGetValue(target, out FrameTargets? targets) || !targets.Ldr.Texture.IsValid)
            return Result<CapturedFrame>.Failure($"nothing has been drawn into {target} yet.");

        return Context.Gpu.Read(targets.Ldr.Texture);
    }

    /// <summary>
    /// A different renderer (a reload, or none), or render settings a context is built from: every instance registered
    /// with the old context is stale, and the old context goes (its handles belonged to the old renderer, which released
    /// them). The renderer gets a context of its own; one whose built-in shaders do not compile draws nothing, logged once.
    /// </summary>
    private void Reset(IRendering? rendering)
    {
        if (ReferenceEquals(rendering, _renderer) && rendering is not null)
            Debugging.Log.Info("Render settings changed: the render state is built again.");

        Forget();
        _renderer = rendering;
        Context = null;
        Stats = default;
        _plan.Clear();
        if (rendering is not null)
        {
            try
            {
                Context = Setup.Create(rendering, _assets, _files, _project);
            }
            catch (InvalidOperationException ex)
            {
                Debugging.Log.Error($"The renderer cannot draw the scene: {ex.Message}");
            }
        }

        _builtWith = BuiltWith.From(_project.Settings.Render);
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
    private void Forget()
    {
        _removed.Clear();
        _changed.Clear();

        if (_registered.Count() == 0)
            return;

        _scratch.Clear();
        _registered.Run((ReadOnlySpan<Handle> entities, Span<RenderInstance> _) => _scratch.AddRange(entities));
        foreach (Handle entity in _scratch)
            _ecs.Remove<RenderInstance>(entity);

        Debugging.Log.Info($"Rendering changed: {_scratch.Count} instance(s) will register again.");
    }

    public void Dispose()
    {
        _onSet.Dispose();
        _onRemoved.Dispose();
        _unregistered.Dispose();
        _instances.Dispose();
        _registered.Dispose();
        _cameras.Dispose();
        _directional.Dispose();
        _points.Dispose();
        _spots.Dispose();
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
