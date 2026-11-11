using System.Collections.Immutable;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Numerics;
using Magic.Assets;
using Magic.Configuration;
using Magic.Debugging;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Files;
using Magic.Graphics;
using Magic.Projects;
using Magic.Threading;
using Magic.Utils;
using Magic.Worlds;

namespace Magic.GpuWorld;

/// <summary>
/// What the world's gem takes each frame: the upscaling, where a gem makes it.
/// </summary>
public readonly record struct GpuWorldInput(Upscaling? Upscaling);

/// <summary>
/// Keeps the world on the GPU for the gems that draw it. <c>LateUpdate</c> copies what the frame draws out of the
/// entities and publishes it; <c>Render</c>, on the render thread, takes the newest published, puts the models,
/// materials, textures, instances, lights and sky's tables on the GPU, uploads the frame's rows, and makes the frame's
/// <see cref="GpuView"/> and its <see cref="GpuInstances"/>. As a domain replaces the world, it forgets the view's
/// history and holds the loading screen until what comes is on the GPU. It is public because nothing in its assembly
/// makes it: Magic does, by reflection, when a project lists the gem.
/// </summary>
public sealed class GpuWorldGem
    : IGem,
        IHasSettings<GpuWorldSettings>,
        IRender<GpuWorldInput, (GpuView, GpuInstances)>,
        IHookIntoDomainLoading,
        IDisposable
{
    private const int DefaultWidth = 1280;

    private const int DefaultHeight = 720;

    private const int AutoRenderWidth = 1920;

    private const int AutoRenderHeight = 1080;

    private readonly IEcs _ecs;

    private readonly AssetManager _assets;

    private readonly Threads _threads;

    private readonly Mailbox<RenderInputs> _inputs = new();

    private readonly GpuResources _resources;

    private readonly InstanceTable _instances;

    /// <summary>
    /// The material a model that failed is drawn with, every slot; an empty slot when there is no failure material.
    /// </summary>
    private readonly MaterialInstance _failure;

    /// <summary>
    /// What is done to the picture when no entity has a <see cref="PostProcessing"/>: the exposure and the AgX tonemap.
    /// </summary>
    private readonly PostProcessing _defaultPost;

    private readonly FrameView _frameView;

    private readonly SkyLuts _sky;

    private readonly LightTiles _lightTiles;

    private readonly Effects _effects;

    /// <summary>
    /// Every shader the gem's passes run, which the GPU provider builds before the frame is made.
    /// </summary>
    private readonly ImmutableArray<ShaderEntry> _shaders;

    private readonly Subscription _preprocessing;

    /// <summary>
    /// The number of the world the frames <see cref="LateUpdate"/> publishes are of, a new one each time a domain
    /// replaces it, so <see cref="Render"/> forgets the view's history at the first frame of what comes. Game thread.
    /// </summary>
    private int _worldNumber;

    /// <summary>
    /// The inputs <see cref="Render"/> uploads, taken from <see cref="_inputs"/>, until newer are. Render thread.
    /// </summary>
    private RenderInputs? _current;

    /// <summary>
    /// Whether the gem makes views: failed once a shader or a pool is not made. Render thread.
    /// </summary>
    private Result _started = Result.Success();

    /// <summary>
    /// What a domain loading needs on the GPU before it shows, until <see cref="Render"/> has uploaded it all. Render
    /// thread.
    /// </summary>
    private Preparing? _preparing;

    /// <summary>
    /// The domains that loaded whose models, materials and effects are on the GPU, or failed: what
    /// <see cref="GpuView.PreparedDomains"/> counts. Render thread.
    /// </summary>
    private int _preparedDomains;

    public GpuWorldGem(
        GpuWorldSettings settings,
        IShaderCompiler shaders,
        AssetManager assets,
        Caches caches,
        IEcs ecs,
        Threads threads
    )
    {
        Settings = settings;
        _ecs = ecs;
        _assets = assets;
        _threads = threads;
        ModelPreprocessor models = new();
        _preprocessing = assets.Register(models);
        _resources = new GpuResources(
            models,
            caches,
            assets,
            shaders,
            threads,
            settings.Anisotropy,
            settings.DroppedTextureLevels
        );
        _failure = new MaterialInstance { Handle = assets.GetFailure<Material, Model>() };
        _defaultPost.Effects[0] = new MaterialInstance { Handle = assets.Find<Material>("Materials/Exposure.mat") };
        _defaultPost.Effects[1] = new MaterialInstance { Handle = assets.Find<Material>("Materials/Agx.mat") };
        _instances = new InstanceTable(assets, _resources, _failure);
        _frameView = new FrameView(_resources);
        _sky = new SkyLuts(assets);
        _lightTiles = new LightTiles(assets);
        _effects = new Effects(assets, _resources);
        _shaders = [.. _sky.Shaders, .. _lightTiles.Shaders];
    }

    /// <summary>
    /// The settings the world is drawn by, which <see cref="LateUpdate"/> hands each frame to <see cref="Render"/>. Game
    /// thread.
    /// </summary>
    public GpuWorldSettings Settings { get; set; }

    public void Dispose()
    {
        _preprocessing.Dispose();
        EndPreparing();
    }

    /// <summary>
    /// Copies what the frame draws out of the entities and publishes it for <see cref="Render"/>.
    /// </summary>
    public void LateUpdate(in Frame frame)
    {
        RenderInputs back = _inputs.Back;
        back.Settings = Settings;
        _ecs.Snapshot(back.Renderers);
        _ecs.Snapshot(back.Cameras);
        _ecs.Snapshot(back.DirectionalLights);
        _ecs.Snapshot(back.PointLights);
        _ecs.Snapshot(back.SpotLights);
        _ecs.Snapshot(back.RectLights);
        _ecs.Snapshot(back.Skies);
        _ecs.Snapshot(back.PostProcessing);
        back.WorldNumber = _worldNumber;
        _inputs.Publish();
    }

    /// <summary>
    /// The newest inputs published, or the last again when none are newer, on the GPU, and the frame's view of them and
    /// its instances; none while the shaders build, the pools are not made, or nothing was published yet.
    /// </summary>
    public (GpuView, GpuInstances)? Render(in Frame frame, RenderGraph graph, in GpuWorldInput input)
    {
        if (!_started.Ok)
            return null;

        // The shaders are the GPU provider's to build, the first ask starting each; one that fails ends the start.
        GpuState shaders = GetState(graph, _shaders.AsSpan());
        if (shaders == GpuState.Failed)
            FailStart("a shader was not built.");

        if (shaders != GpuState.Ready)
            return null;

        // The pools, with the models made since the last frame settled into them, before any renderer's row is built.
        // Every model asked for from here on is asked at or after the frame's start, which eviction keeps.
        long frameStart = Environment.TickCount64;
        Result pools = _resources.Prepare(graph);
        if (!pools.Ok)
        {
            FailStart(pools.Error);

            return null;
        }

        if (_inputs.TryTake(out RenderInputs? newest))
        {
            // The first frame of another world forgets last frame's camera and size, so nothing drawn before carries
            // into it.
            if (newest.WorldNumber != _current?.WorldNumber)
                _frameView.ClearHistory();

            if (_current is not null)
                _inputs.Return(_current);

            _current = newest;
        }

        if (_current is not { } inputs)
            return null;

        // Entities -> resources -> rows: the renderers written since the inputs before ask for their models and have
        // their rows built again, and the materials' rows are made again at upload.
        int instanceCount = _instances.Fill(inputs.Renderers, frame.Index);

        // The loading domain shows once what it draws and its effects are on the GPU.
        if (_preparing is { } preparing && IsPrepared(graph, preparing))
        {
            Log.Info(
                $"What the domain draws is on the GPU: {preparing.Models.Length} models, {preparing.Materials.Length} materials."
            );
            EndPreparing();
        }

        // A model no renderer drawn holds, and nothing asked for this frame, leaves once it is kept long enough; the
        // loading domain's models were just asked for, so they stay while it waits.
        _resources.EvictUnusedModels(
            inputs.Renderers.Second[..instanceCount],
            frameStart,
            inputs.Settings.ModelKeepSec
        );

        _resources.Upload(graph);

        // The picture's effects; the camera jitters while one of them asks for it, or while a gem upscales.
        Result<(ReadOnlyMemory<GpuEffect> Effects, GpuBuffer Rows)> effects = _effects.Resolve(
            graph,
            GetEffects(inputs)
        );
        if (!effects.Ok)
        {
            Log.Error($"The frame's view is not made: {effects.Error}");

            return null;
        }

        bool isJittered = input.Upscaling is not null;
        foreach (GpuEffect effect in effects.Value.Effects.Span)
        {
            isJittered |= effect.State == GpuState.Ready && effect.Needs.HasJitter;
        }

        Vector2 jitter = isJittered ? FrameView.GetJitter(frame.Index) : Vector2.Zero;

        Result<GpuView> view = UploadFrame(graph, inputs, frame, jitter, instanceCount, effects.Value);
        if (!view.Ok)
        {
            Log.Error($"The frame's view is not made: {view.Error}");

            return null;
        }

        // The sky's tables while there is a sky, and each tile's lights.
        GpuConstants header = new(view.Value.Frame.Id);
        SkyTables sky = new(view.Value.SkyTransmittance, view.Value.SkyMultiScattering, view.Value.SkyView);
        if (inputs.Skies.Count > 0)
            _sky.Record(graph, header, view.Value.Frame, sky);

        _lightTiles.Record(
            graph,
            view.Value,
            header,
            new LightBuffers(view.Value.LightTable, view.Value.LightTiles, view.Value.LightCount)
        );
        return (view.Value, _instances.Instances);
    }

    /// <summary>
    /// Numbers the world anew, so <see cref="Render"/> forgets the view's history at the first frame of what comes. On
    /// the game thread, without leaving it, so the world goes on to what comes before another frame is published.
    /// </summary>
    public Task OnDomainUnloadedAsync(CancellationToken cancel)
    {
        _worldNumber++;

        return Task.CompletedTask;
    }

    /// <summary>
    /// Asks for every model and material <paramref name="entities"/> draw, and the effects of their first post
    /// processing, or the default's, and ends in the frame they are on the GPU, or failed and drawn as the
    /// failure or left out. Materials are asked for at once; models once the pools are made, and the effects, by
    /// <see cref="Render"/>. One that comes while another waits ends that one. On the render thread, whose tables
    /// these are.
    /// </summary>
    public async Task OnDomainLoadingAsync(
        ImmutableArray<ImmutableArray<IComponent>> entities,
        CancellationToken cancel
    )
    {
        await _threads.Enter(EngineThread.Render);
        EndPreparing();
        if (!_started.Ok)
        {
            _preparedDomains++;

            return;
        }

        Renderer[] renderers =
        [
            .. entities
                .SelectMany(entity => entity.OfType<Renderer>())
                .Where(renderer => renderer.Model != Handle<Model>.None),
        ];
        ImmutableArray<Handle<Material>> materials =
        [
            .. renderers
                .SelectMany(renderer => renderer.Materials.All.Select(slot => slot.Handle))
                .Where(material => material != Handle<Material>.None)
                .Distinct(),
        ];
        foreach (Handle<Material> material in materials)
        {
            _resources.Get(material);
        }

        // The effects as Render runs them: the first post processing's, or the default's.
        MaterialSlots post = entities
            .SelectMany(entity => entity.OfType<PostProcessing>())
            .Select(processing => processing.Effects)
            .DefaultIfEmpty(_defaultPost.Effects)
            .First();

        Preparing preparing = new(
            [.. renderers.Select(renderer => renderer.Model).Distinct()],
            materials,
            post,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        );
        _preparing = preparing;
        await preparing.Done.Task.WaitAsync(cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// Where <paramref name="shaders"/> are: failed if any failed, loading while any loads, else ready. Each is asked,
    /// so the first ask starts them all.
    /// </summary>
    private static GpuState GetState(RenderGraph graph, ReadOnlySpan<ShaderEntry> shaders)
    {
        GpuState all = GpuState.Ready;
        foreach (ShaderEntry shader in shaders)
        {
            GpuState state = graph.GetState(shader);
            if (state == GpuState.Failed)
                all = GpuState.Failed;
            else if (state == GpuState.Loading && all == GpuState.Ready)
                all = GpuState.Loading;
        }

        return all;
    }

    /// <summary>
    /// The first camera that draws into the window, else one at the origin.
    /// </summary>
    private static (Transform Transform, Camera Camera) GetCamera(EcsSnapshot<Transform, Camera> cameras)
    {
        for (int i = 0; i < cameras.Count; i++)
        {
            if (cameras.Second[i].Target.IsWindow)
                return (cameras.First[i], cameras.Second[i]);
        }

        return (new Transform(), new Camera());
    }

    /// <summary>
    /// The directional light the frame is lit by: the sun while it is up, else the moon while it is up, else the first
    /// there is, else a white light from above: towards it, unit, and its colour times its intensity, in lux, above the
    /// air.
    /// </summary>
    private static (Vector3 Direction, Vector3 Illuminance) GetSun(EcsSnapshot<Transform, DirectionalLight> lights)
    {
        (Vector3 Direction, Vector3 Illuminance) sun = (Vector3.Normalize(new Vector3(0.3f, 0.8f, -0.5f)), Vector3.One);
        int sunRank = -1;
        for (int i = 0; i < lights.Count; i++)
        {
            DirectionalLight light = lights.Second[i];
            Vector3 towards = -Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, lights.First[i].Rotation));
            bool isUp = towards.Y > 0;
            int rank = 0;
            if (isUp && light.Mode == DirectionalLightMode.Sun)
                rank = 2;
            else if (isUp && light.Mode == DirectionalLightMode.Moon)
                rank = 1;

            if (rank <= sunRank)
                continue;

            Vector3 illuminance = new Vector3(light.Color.R, light.Color.G, light.Color.B) * light.Intensity;
            sun = (towards, illuminance);
            sunRank = rank;
        }

        return sun;
    }

    /// <summary>
    /// The effects the frame runs: the first post processing's, or the engine's, exposed by the exposure compensation;
    /// each whose material the settings turn off left out.
    /// </summary>
    private MaterialSlots GetEffects(RenderInputs inputs)
    {
        MaterialSlots effects;
        if (inputs.PostProcessing.Count > 0)
            effects = inputs.PostProcessing.First[0].Effects;
        else
        {
            effects = _defaultPost.Effects;
            effects[0].Overrides["compensation"] = inputs.Settings.ExposureCompensation;
        }

        // Most frames turn nothing off, so they name no material.
        ImmutableArray<string> disabled = inputs.Settings.DisabledEffects;
        if (disabled.IsEmpty)
            return effects;

        for (int place = 0; place < MaterialSlots.Capacity; place++)
        {
            if (
                _assets.GetPath(effects[place].Handle) is { } path
                && disabled.Contains(Path.GetFileNameWithoutExtension(path))
            )
                effects[place] = default;
        }

        return effects;
    }

    /// <summary>
    /// Asks for the models and the effects <paramref name="preparing"/> waits for, which the pools only take once they are
    /// made, and for the failure material a model that failed is drawn with, an ask for one asked for already doing
    /// nothing; then whether every one of them and every material is on the GPU, or failed: drawn as the failure, which
    /// is on the GPU too, or, for an effect, left out.
    /// </summary>
    private bool IsPrepared(RenderGraph graph, Preparing preparing)
    {
        bool isPrepared = !_effects.IsLoading(graph, preparing.Effects);
        bool hasFailure = false;
        foreach (Handle<Model> model in preparing.Models)
        {
            _resources.Get(model);
            isPrepared &= _resources.HasArrived(model);
            hasFailure |= _resources.HasFailed(model);
        }

        foreach (Handle<Material> material in preparing.Materials)
        {
            isPrepared &= _resources.Get(material) != ResourceState.Loading;
        }

        if (!hasFailure || _failure.Handle == Handle<Material>.None)
            return isPrepared;

        return _resources.Get(_failure.Handle) != ResourceState.Loading && isPrepared;
    }

    /// <summary>
    /// Lets the domain waiting on what it draws show, and counts it prepared.
    /// </summary>
    private void EndPreparing()
    {
        if (_preparing is null)
            return;

        _preparing.Done.TrySetResult();
        _preparing = null;
        _preparedDomains++;
    }

    /// <summary>
    /// Ends the start as failed: the gem makes no view, and holds no domain back.
    /// </summary>
    private void FailStart(string error)
    {
        Log.Error($"The world is not put on the GPU: {error}");
        EndPreparing();
        _started = Result.Failure(error);
    }

    /// <summary>
    /// The frame's tables and rows uploaded, each asking for what it uploads into, and the view that names them; what
    /// was not made, and no view is.
    /// </summary>
    private Result<GpuView> UploadFrame(
        RenderGraph graph,
        RenderInputs inputs,
        in Frame frame,
        Vector2 jitter,
        int instanceCount,
        (ReadOnlyMemory<GpuEffect> Effects, GpuBuffer Rows) effects
    )
    {
        Result<(GpuBuffer Rows, GpuBuffer Materials)> tables = _instances.Upload(graph);
        if (!tables.Ok)
            return Result.Failure(tables.Error);

        // The finished picture at the resolution, or at the window's size when it has no side, and 1280 by 720 with no
        // window; the world drawn at the render scale of it, or when that is auto at the most that fits 1920 by 1080,
        // never more than all of it.
        Size shown = inputs.Settings.Resolution;
        if (shown.Width <= 0 || shown.Height <= 0)
        {
            TextureDescription window = graph.WindowTarget.IsNone ? default : graph.GetDescription(graph.WindowTarget);
            shown = window.Width > 0 ? new Size(window.Width, window.Height) : new Size(DefaultWidth, DefaultHeight);
        }

        float scale = MathF.Min(
            1,
            inputs.Settings.RenderScale > 0
                ? inputs.Settings.RenderScale
                : MathF.Min((float)AutoRenderWidth / shown.Width, (float)AutoRenderHeight / shown.Height)
        );
        int width = Math.Max(1, (int)MathF.Round(shown.Width * scale));
        int height = Math.Max(1, (int)MathF.Round(shown.Height * scale));

        (Transform Transform, Camera Camera) camera = GetCamera(inputs.Cameras);
        if (inputs.Settings.FieldOfView > 0)
            camera.Camera = camera.Camera with { FieldOfView = inputs.Settings.FieldOfView };

        // The targets, the lights and the sky's tables asked for in that order, then the frame's rows written.
        Result<FrameTargets> targets = _frameView.TryGetTargets(graph, width, height);
        if (!targets.Ok)
            return Result.Failure(targets.Error);

        Result<LightBuffers> lights = _lightTiles.Prepare(
            graph,
            width,
            height,
            inputs.PointLights,
            inputs.SpotLights,
            inputs.RectLights
        );
        if (!lights.Ok)
            return Result.Failure(lights.Error);

        Result<SkyTables> sky = SkyLuts.Prepare(graph);
        if (!sky.Ok)
            return Result.Failure(sky.Error);

        Result uploaded = _frameView.Upload(
            graph,
            targets.Value,
            camera,
            frame,
            jitter,
            instanceCount,
            tables.Value,
            GetSun(inputs.DirectionalLights),
            inputs.Skies.Count > 0,
            inputs.Settings.SkyBrightness,
            sky.Value,
            lights.Value
        );
        if (!uploaded.Ok)
            return Result.Failure(uploaded.Error);

        FrameTargets view = targets.Value;

        return Result.Success(
            new GpuView
            {
                Frame = view.Constants,
                Width = view.Width,
                Height = view.Height,
                ShownWidth = shown.Width,
                ShownHeight = shown.Height,
                HasHistory = view.HasHistory,
                Depth = view.Depth,
                GBufferNormal = view.GBufferNormal,
                GBufferBase = view.GBufferBase,
                GBufferSurface = view.GBufferSurface,
                Velocity = view.Velocity,
                GlassDepth = view.GlassDepth,
                GlassBase = view.GlassBase,
                GlassNormal = view.GlassNormal,
                GlassSurface = view.GlassSurface,
                Hdr = view.Hdr,
                Output = view.Output,
                Instances = tables.Value.Rows,
                InstanceCount = instanceCount,
                InstanceCapacity = _instances.Capacity,
                Materials = tables.Value.Materials,
                Vertices = _resources.VertexPool,
                Indices = _resources.IndexPool,
                Meshes = _resources.Meshes,
                Models = _resources.Models,
                MaterialShaders = _resources.MaterialShaders,
                HeapTextures = _resources.GetHeapTextures(),
                LightTable = lights.Value.Table,
                LightTiles = lights.Value.Tiles,
                LightCount = lights.Value.Count,
                SkyTransmittance = sky.Value.Transmittance,
                SkyMultiScattering = sky.Value.MultiScattering,
                SkyView = sky.Value.View,
                Effects = effects.Effects,
                EffectRows = effects.Rows,
                PreparedDomains = _preparedDomains,
            }
        );
    }

    /// <summary>
    /// The models and materials a domain loading draws, the effects its frames run, and what ends once they are all on
    /// the GPU.
    /// </summary>
    private sealed record Preparing(
        ImmutableArray<Handle<Model>> Models,
        ImmutableArray<Handle<Material>> Materials,
        MaterialSlots Effects,
        TaskCompletionSource Done
    );
}
