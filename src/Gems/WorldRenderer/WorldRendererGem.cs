using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.InteropServices;
using Magic;
using Magic.Assets;
using Magic.Assets.Pipeline;
using Magic.Assets.Types;
using Magic.Debugging;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Events;
using Magic.Files;
using Magic.Gems;
using Magic.Gpu;
using Magic.Images;
using Magic.Pipelines;
using Magic.Rendering;
using Magic.Shaders;
using Magic.Threading;
using Magic.Utils;

namespace WorldRenderer;

/// <summary>
/// Draws the world. <c>Update</c> reads the renderers, the camera and the sun out of the entities into a copy;
/// <c>Render</c> uploads what changed in the frame's tables, captures the models newly resident into their imposter
/// atlases, culls every instance's clusters on the GPU against the frustum, their cones, the pixel error threshold
/// and last frame's depth pyramid, draws the visible ones into a visibility buffer, retests what the old pyramid hid
/// against the new one, resolves every pixel's material into the GBuffer, lights it with the sun, shows it in the
/// window and saves it when a screenshot is asked for. It is public because nothing in its assembly makes it: Magic
/// does, by reflection, when a project lists the gem.
/// </summary>
public sealed class WorldRendererGem(
    IGraphicsApi gpu,
    IShaderCompiler shaders,
    AssetDatabase assets,
    IMeshPreprocessor meshes,
    IEcs ecs,
    IFileSystem files,
    Threads threads,
    App app
) : IGem, IPipeline<Startup>, IPipeline<Update>, IPipeline<Render>, IDisposable
{
    private const int MaxVisibleClusters = 1 << 18;
    private const int CountsBytes = 64;
    private const int DrawArgumentsBytes = 16;
    private const int RetestArgumentsBytes = 12;
    private const int DefaultWidth = 1280;
    private const int DefaultHeight = 720;
    private const int CapturesPerFrame = 4;
    private const int CaptureCellCount = GpuCapture.Cells * GpuCapture.Cells;
    private const int CullGroupsPerRow = 65535;
    private const float ErrorThresholdPixels = 1;
    private const float SkyBrightness = 0.3f;

    /// <summary>
    /// What a surface facing the sun lands at in the picture before the tonemap, with no meter yet.
    /// </summary>
    private const float ExposureAtSun = 4;

    private readonly RenderGraph _graph = new(gpu);
    private readonly RenderWorld _world = new();
    private readonly GeometryPools _pools = new(gpu, meshes, assets, threads, new DistanceField(gpu));
    private readonly TextureTable _textures = new(gpu, assets, threads);
    private readonly FrameTargets _targets = new(gpu);
    private readonly FrameTargets _cellTargets = new(gpu);
    private readonly RendererPipelines _pipelines = new(gpu, shaders, assets);
    private readonly InstanceTable _instances = new(gpu);
    private readonly CaptureTable _captures = new(gpu);
    private readonly SunShadows _shadows = new(gpu);
    private readonly Lighting _lighting = new(gpu);
    private readonly GpuBuffer[] _cellConstants = new GpuBuffer[CaptureCellCount];
    private MaterialTable? _materials;
    private GpuBuffer _constants;
    private GpuBuffer _captureInstance;
    private GpuBuffer _counts;
    private GpuBuffer _retestArguments;
    private GpuBuffer _visibleClusters;
    private GpuBuffer _drawArguments;
    private GpuBuffer _retestList;
    private GpuSampler _sampler;
    private GpuSampler _captureSampler;
    private bool _isReady;
    private bool _hasHzb;
    private DebugView _debugView;

    public void Dispose()
    {
        _graph.Clear();
        _pipelines.Dispose();
        _targets.Dispose();
        _cellTargets.Dispose();
        _materials?.Dispose();
        _textures.Dispose();
        _pools.Dispose();
        _instances.Dispose();
        _captures.Dispose();
        _shadows.Dispose();
        _lighting.Dispose();
        foreach (
            GpuBuffer buffer in (ReadOnlySpan<GpuBuffer>)
                [
                    _constants,
                    _captureInstance,
                    _counts,
                    _retestArguments,
                    _visibleClusters,
                    _drawArguments,
                    _retestList,
                    .. _cellConstants,
                ]
        )
        {
            if (!buffer.IsNone)
                gpu.Destroy(buffer);
        }

        if (!_sampler.IsNone)
            gpu.Destroy(_sampler);

        if (!_captureSampler.IsNone)
            gpu.Destroy(_captureSampler);
    }

    /// <summary>
    /// Compiles the passes and makes the pools and tables. It runs to the end even once the run was told to exit, so
    /// a run that only validates still finds a shader that does not compile.
    /// </summary>
    public async ValueTask<Startup> RunAsync(Startup state, CancellationToken cancel)
    {
        Result<ImmutableArray<CompiledShader>> compiled = await _pipelines
            .CompileAsync(CancellationToken.None)
            .ConfigureAwait(false);
        await threads.Enter(EngineThread.Main);
        if (!compiled.Ok)
        {
            Log.Error($"The renderer draws nothing: {compiled.Error}");

            return state;
        }

        _materials = new MaterialTable(gpu, assets, _textures, threads);
        Result made = _pipelines.Create(compiled.Value);
        foreach (
            Func<Result> step in (ReadOnlySpan<Func<Result>>)
                [
                    _pools.Create,
                    _materials.Create,
                    _instances.Create,
                    _captures.Create,
                    _shadows.Create,
                    _lighting.Create,
                    _pools.Field.Create,
                    CreateBuffers,
                    () => _cellTargets.Resize(GpuCapture.CellSize, GpuCapture.CellSize, _graph),
                ]
        )
        {
            if (made.Ok)
                made = step();
        }

        if (!made.Ok)
        {
            Log.Error($"The renderer draws nothing: {made.Error}");

            return state;
        }

        _isReady = true;

        return state;
    }

    /// <summary>
    /// Reads what the frame draws out of the entities.
    /// </summary>
    public ValueTask<Update> RunAsync(Update state, CancellationToken cancel)
    {
        _world.Clear();
        foreach (EcsChunk<Transform, Renderer> chunk in ecs.Query<Transform, Renderer>())
        {
            for (int i = 0; i < chunk.Entities.Length; i++)
            {
                Renderer renderer = chunk.Second[i];
                if (renderer.Model != Handle<Model>.None)
                {
                    _world.Instances.Add(
                        new RenderInstance(chunk.Entities[i], renderer.Model, renderer.Materials, chunk.First[i])
                    );
                }
            }
        }

        foreach (EcsChunk<Transform, Camera> chunk in ecs.Query<Transform, Camera>())
        {
            for (int i = 0; i < chunk.Entities.Length && _world.Camera is null; i++)
            {
                Camera camera = chunk.Second[i];
                if (camera.Target.IsWindow)
                    _world.Camera = new RenderCamera(chunk.First[i], camera.FieldOfView, camera.Near);
            }
        }

        foreach (EcsChunk<Transform, DirectionalLight> chunk in ecs.Query<Transform, DirectionalLight>())
        {
            for (int i = 0; i < chunk.Entities.Length && _world.Sun is null; i++)
            {
                DirectionalLight light = chunk.Second[i];
                Vector3 towards = -Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, chunk.First[i].Rotation));
                Vector3 illuminance = new Vector3(light.Color.R, light.Color.G, light.Color.B) * light.Intensity;
                float exposure = light.Intensity > 0 ? ExposureAtSun / light.Intensity : 1;
                _world.Sun = new RenderSun(towards, illuminance, exposure);
            }
        }

        foreach (EcsChunk<Transform, PointLight> chunk in ecs.Query<Transform, PointLight>())
        {
            for (int i = 0; i < chunk.Entities.Length; i++)
            {
                PointLight light = chunk.Second[i];
                _world.Lights.Add(
                    new GpuLight
                    {
                        Position = chunk.First[i].Position,
                        Range = light.Reach,
                        Direction = Vector3.UnitZ,
                        Radius = light.Radius,
                        Radiance = new Vector3(light.Color.R, light.Color.G, light.Color.B) * light.Intensity,
                        Type = GpuLight.Point,
                    }
                );
            }
        }

        foreach (EcsChunk<Transform, SpotLight> chunk in ecs.Query<Transform, SpotLight>())
        {
            for (int i = 0; i < chunk.Entities.Length; i++)
            {
                SpotLight light = chunk.Second[i];
                _world.Lights.Add(
                    new GpuLight
                    {
                        Position = chunk.First[i].Position,
                        Range = light.Reach,
                        Direction = Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, chunk.First[i].Rotation)),
                        Radius = light.Radius,
                        Radiance = new Vector3(light.Color.R, light.Color.G, light.Color.B) * light.Intensity,
                        Type = GpuLight.Spot,
                        CosOuter = MathF.Cos(light.OuterAngle * MathF.PI / 180),
                        CosInner = MathF.Cos(light.FadeAngle * MathF.PI / 180),
                    }
                );
            }
        }

        foreach (EcsChunk<Transform, RectLight> chunk in ecs.Query<Transform, RectLight>())
        {
            for (int i = 0; i < chunk.Entities.Length; i++)
            {
                RectLight light = chunk.Second[i];
                Transform at = chunk.First[i];
                _world.Lights.Add(
                    new GpuLight
                    {
                        Position = at.Position,
                        Range = light.Reach,
                        Direction = Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, at.Rotation)),
                        Radius = 0.01f,
                        Radiance = new Vector3(light.Color.R, light.Color.G, light.Color.B) * light.Intensity,
                        Type = GpuLight.Rect,
                        Width = light.Width * Math.Abs(at.Scale.X),
                        Height = light.Height * Math.Abs(at.Scale.Y),
                    }
                );
            }
        }

        foreach (EcsChunk<Fog> chunk in ecs.Query<Fog>())
        {
            if (chunk.Entities.Length > 0 && _world.Fog is null)
                _world.Fog = chunk.First[0];
        }

        foreach (EcsChunk<Sky> chunk in ecs.Query<Sky>())
        {
            _world.HasSky |= chunk.Entities.Length > 0;
        }

        foreach (SettingChanged setting in state.Frame.Events.Get<SettingChanged>())
        {
            if (setting.Key != "WorldRenderer.debug")
                continue;

            if (Enum.TryParse(setting.Value, ignoreCase: true, out DebugView view))
                _debugView = view;
            else
                Log.Error(
                    $"{setting.Key} takes {string.Join(", ", Enum.GetNames<DebugView>())}, not '{setting.Value}'."
                );
        }

        return ValueTask.FromResult(state);
    }

    /// <summary>
    /// Draws the frame and submits it.
    /// </summary>
    public ValueTask<Render> RunAsync(Render state, CancellationToken cancel)
    {
        if (!_isReady || _materials is null)
            return ValueTask.FromResult(state);

        int width = app.Window is { Width: > 0, Height: > 0 } window ? window.Width : DefaultWidth;
        int height = app.Window is { Width: > 0, Height: > 0 } sized ? sized.Height : DefaultHeight;
        if (_targets.Width != width || _targets.Height != height)
        {
            Result resized = _targets.Resize(width, height, _graph);
            if (!resized.Ok)
            {
                Log.Error($"The frame is not drawn: {resized.Error}");

                return ValueTask.FromResult(state);
            }

            _hasHzb = false;
        }

        // What arrived: models into the pools, textures, material rows, capture rows.
        _materials.Refresh();
        _graph.BeginTransferPass("Upload arrivals");
        _pools.Upload(_graph);
        _textures.Upload(_graph);
        _materials.Upload(_graph);
        _graph.EndPass();

        // The captures newly asked for, drawn before any instance shows them.
        int captured = 0;
        while (captured < CapturesPerFrame && _captures.TryTakeJob(out CaptureJob job))
        {
            RecordCapture(job);
            captured++;
        }

        // The frame's tables.
        RenderCamera camera = _world.Camera ?? new RenderCamera(new Transform(), 60, 0.05f);
        RenderSun sun = _world.Sun ?? new RenderSun(Vector3.Normalize(new Vector3(0.3f, 0.8f, -0.5f)), Vector3.One, 1);
        int instanceCount = UpdateInstances();
        _graph.BeginTransferPass("Upload frame");
        _captures.Upload(_graph);
        _instances.Upload(_graph);
        UploadConstants(_constants, GetFrameConstants(camera, sun, state.Frame.Index, instanceCount));
        ResetCounts();
        _shadows.Prepare(
            _graph,
            camera.Transform.Position,
            sun.Direction,
            2 * MathF.Tan(camera.FieldOfView * MathF.PI / 360) / _targets.Height,
            _instances.Changes
        );
        _lighting.Prepare(_graph, _world, camera, sun, _targets, _debugView);
        _pools.Field.Prepare(
            _graph,
            _pipelines,
            _constants,
            camera.Transform.Position,
            _instances.Changes,
            instanceCount
        );
        _instances.ClearChanges();
        _graph.EndPass();
        _pools.Field.Record(_graph, _pipelines, _constants, _instances.Buffer);
        _lighting.RecordTables(_graph, _pipelines, _constants, _shadows);

        RenderView view = new(_constants, _instances.Buffer, instanceCount, _targets);
        RecordCull(view, "Cull clusters", mode: 0);
        RecordDraw(view, "Draw visibility", TargetLoad.Clear, drawsOffset: 0, countOffset: 0);
        RecordHzb();
        RecordCull(view, "Retest clusters", mode: 1);
        RecordDraw(
            view,
            "Draw retested",
            TargetLoad.Keep,
            drawsOffset: (ulong)MaxVisibleClusters * DrawArgumentsBytes,
            countOffset: 8
        );
        RecordHzb();
        _shadows.Record(_graph, _pipelines, _constants, _instances.Buffer, instanceCount, _pools, _targets);
        RecordResolve(view, "Resolve materials");
        _lighting.RecordShading(_graph, _pipelines, _constants, _shadows, _pools.Field, _targets);

        GpuTexture target = gpu.WindowTarget;
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
        ReadOnlySpan<ScreenshotRequested> requests = state.Frame.Events.Get<ScreenshotRequested>();
        if (requests.Length > 0)
        {
            _graph.BeginTransferPass("Read the output back");
            _graph.Use(_targets.Output, GpuAccess.CopySource);
            _graph.Use(_targets.Readback, GpuAccess.CopyDestination);
            _graph.Recorder.CopyTextureToBuffer(_targets.Output, 0, 0, _targets.Readback, 0);
            _graph.Use(_targets.Readback, GpuAccess.HostRead);
            _graph.EndPass();
        }

        gpu.Submit(_graph.Finish());
        _hasHzb = true;
        if (requests.Length > 0)
            SaveScreenshotAsync(requests[^1].Path, width, height).FireAndForget();

        return ValueTask.FromResult(state);
    }

    /// <summary>
    /// The frustum's planes from the view-projection, for row vectors: each a column added to or taken from the last,
    /// scaled so its normal is a unit; the far plane is the third column alone, which a perspective with no far plane
    /// never rejects by.
    /// </summary>
    private static void FillFrustum(in Matrix4x4 viewProjection, ref FrameConstants frame)
    {
        Vector4 c0 = new(viewProjection.M11, viewProjection.M21, viewProjection.M31, viewProjection.M41);
        Vector4 c1 = new(viewProjection.M12, viewProjection.M22, viewProjection.M32, viewProjection.M42);
        Vector4 c2 = new(viewProjection.M13, viewProjection.M23, viewProjection.M33, viewProjection.M43);
        Vector4 c3 = new(viewProjection.M14, viewProjection.M24, viewProjection.M34, viewProjection.M44);
        frame.FrustumPlane0 = Normalize(c3 + c0);
        frame.FrustumPlane1 = Normalize(c3 - c0);
        frame.FrustumPlane2 = Normalize(c3 + c1);
        frame.FrustumPlane3 = Normalize(c3 - c1);
        frame.FrustumPlane4 = Normalize(c3 - c2);
        frame.FrustumPlane5 = Normalize(c2);
    }

    private static Vector4 Normalize(Vector4 plane)
    {
        float length = MathF.Sqrt((plane.X * plane.X) + (plane.Y * plane.Y) + (plane.Z * plane.Z));

        return length > 0 ? plane / length : plane;
    }

    /// <summary>
    /// A perspective with no far plane and reversed depth, for row vectors: depth is the near distance over the view
    /// depth, 1 at the near plane and 0 at infinity.
    /// </summary>
    private static Matrix4x4 CreatePerspective(float fieldOfViewDegrees, float aspect, float near)
    {
        float focal = 1 / MathF.Tan(fieldOfViewDegrees * MathF.PI / 360);
        Matrix4x4 projection = default;
        projection.M11 = focal / aspect;
        projection.M22 = focal;
        projection.M34 = 1;
        projection.M43 = near;

        return projection;
    }

    /// <summary>
    /// An orthographic projection of a sphere of <paramref name="radius"/> whose centre is 2 radii ahead, for row
    /// vectors, with reversed depth: 1 at the sphere's nearest point and 0 at its farthest.
    /// </summary>
    private static Matrix4x4 CreateCaptureProjection(float radius)
    {
        Matrix4x4 projection = default;
        projection.M11 = 1 / radius;
        projection.M22 = 1 / radius;
        projection.M33 = -1 / (2 * radius);
        projection.M43 = 1.5f;
        projection.M44 = 1;

        return projection;
    }

    /// <summary>
    /// A unit vector from its octahedral point in [-1, 1]², as <c>Core/Pack.slang</c> decodes it.
    /// </summary>
    private static Vector3 DecodeOctahedral(Vector2 flat)
    {
        Vector3 n = new(flat.X, flat.Y, 1 - Math.Abs(flat.X) - Math.Abs(flat.Y));
        float t = Math.Clamp(-n.Z, 0, 1);
        n.X += n.X >= 0 ? -t : t;
        n.Y += n.Y >= 0 ? -t : t;

        return Vector3.Normalize(n);
    }

    private Result CreateBuffers()
    {
        Result<GpuSampler> sampler = gpu.CreateSampler(new SamplerDescription { Anisotropy = 8 });
        if (!sampler.Ok)
            return Result.Failure($"the default sampler was not made: {sampler.Error}");

        _sampler = sampler.Value;
        Result<GpuSampler> captureSampler = gpu.CreateSampler(
            new SamplerDescription { Wrap = TextureWrap.Clamp, HasMipmapBlend = false }
        );
        if (!captureSampler.Ok)
            return Result.Failure($"the capture sampler was not made: {captureSampler.Error}");

        _captureSampler = captureSampler.Value;
        (string Name, long Bytes, BufferUsages Usage)[] descriptions =
        [
            ("Frame constants", FrameConstants.Bytes, BufferUsages.Storage),
            ("Capture instance", GpuInstance.Bytes, BufferUsages.Storage),
            ("Counts", CountsBytes, BufferUsages.Storage | BufferUsages.Indirect),
            ("Retest arguments", RetestArgumentsBytes, BufferUsages.Storage | BufferUsages.Indirect),
            ("Visible clusters", (long)MaxVisibleClusters * 8, BufferUsages.Storage),
            (
                "Draw arguments",
                (long)MaxVisibleClusters * 2 * DrawArgumentsBytes,
                BufferUsages.Storage | BufferUsages.Indirect
            ),
            ("Retest list", (long)MaxVisibleClusters * 8, BufferUsages.Storage),
        ];
        GpuBuffer[] made = new GpuBuffer[descriptions.Length];
        for (int i = 0; i < descriptions.Length; i++)
        {
            Result<GpuBuffer> buffer = gpu.CreateBuffer(
                new BufferDescription
                {
                    Bytes = descriptions[i].Bytes,
                    Usage = descriptions[i].Usage,
                    Name = descriptions[i].Name,
                }
            );
            if (!buffer.Ok)
                return Result.Failure($"the {descriptions[i].Name} buffer was not made: {buffer.Error}");

            made[i] = buffer.Value;
        }

        (_constants, _captureInstance, _counts, _retestArguments, _visibleClusters, _drawArguments, _retestList) = (
            made[0],
            made[1],
            made[2],
            made[3],
            made[4],
            made[5],
            made[6]
        );
        for (int cell = 0; cell < CaptureCellCount; cell++)
        {
            Result<GpuBuffer> buffer = gpu.CreateBuffer(
                new BufferDescription
                {
                    Bytes = FrameConstants.Bytes,
                    Usage = BufferUsages.Storage,
                    Name = $"Capture constants {cell}",
                }
            );
            if (!buffer.Ok)
                return Result.Failure($"a capture's constants buffer was not made: {buffer.Error}");

            _cellConstants[cell] = buffer.Value;
        }

        return Result.Success();
    }

    /// <summary>
    /// Every renderer's row in the instance table: its model's place in the pools, its material rows and its
    /// capture, or an empty row while its model is being made. How many rows the cull walks.
    /// </summary>
    private int UpdateInstances()
    {
        if (_materials is null)
            return 0;

        Handle<Material> failureMaterial = assets.GetFailure<Material, Model>();
        Span<uint> materialRows = stackalloc uint[MaterialSlots.Capacity];
        _instances.Begin(unchecked((uint)Environment.TickCount));
        foreach (RenderInstance instance in _world.Instances)
        {
            if (_pools.Get(instance.Model) is not { } resident)
            {
                _instances.Set(instance.Entity, default);
                continue;
            }

            MaterialSlots materials = instance.Materials;
            ReadOnlySpan<Handle<Material>> slots = materials;
            bool isSettled = true;
            for (int slot = 0; slot < MaterialSlots.Capacity; slot++)
            {
                materialRows[slot] = _materials.GetRow(resident.IsFailure ? failureMaterial : slots[slot]);
                isSettled &= _materials.IsSettled(materialRows[slot]);
            }

            Matrix4x4 world = instance.Transform.Matrix;
            Matrix4x4.Invert(world, out Matrix4x4 inverseWorld);
            Vector3 scale = instance.Transform.Scale;
            float maxScale = Math.Max(Math.Abs(scale.X), Math.Max(Math.Abs(scale.Y), Math.Abs(scale.Z)));
            uint capture = isSettled ? _captures.Get(instance.Model.Id, resident, materialRows) : 0;
            _instances.Set(
                instance.Entity,
                new GpuInstance
                {
                    World = world,
                    InverseWorld = inverseWorld,
                    PreviousWorld = world,
                    BoundsCenter = Vector3.Transform(resident.BoundsCenter, world),
                    BoundsRadius = resident.BoundsRadius * maxScale,
                    ClusterOffset = resident.ClusterOffset,
                    ClusterCount = resident.ClusterCount,
                    Flags = resident.FieldSlot,
                    Capture = capture,
                    Material0 = materialRows[0],
                    Material1 = materialRows[1],
                    Material2 = materialRows[2],
                    Material3 = materialRows[3],
                    Material4 = materialRows[4],
                    Material5 = materialRows[5],
                    Material6 = materialRows[6],
                    Material7 = materialRows[7],
                }
            );
        }

        _instances.End();

        return _instances.Count;
    }

    /// <summary>
    /// The main view's constants: the camera, the sun and the frame's resources.
    /// </summary>
    private FrameConstants GetFrameConstants(RenderCamera camera, RenderSun sun, uint frameIndex, int instanceCount)
    {
        float aspect = (float)_targets.Width / _targets.Height;
        Matrix4x4.Invert(Matrix4x4.CreateFromQuaternion(camera.Transform.Rotation), out Matrix4x4 view);
        Matrix4x4 projection = CreatePerspective(camera.FieldOfView, aspect, camera.Near);
        FrameConstants frame = GetViewConstants(
            view,
            projection,
            camera.Transform.Position,
            camera.Near,
            _targets,
            _instances.Buffer,
            instanceCount
        );
        frame.SunDirection = new Vector4(sun.Direction, 0);
        frame.SunRadiance = new Vector4(sun.Radiance, 0);
        frame.SkyRadiance = new Vector4(SkyBrightness * 0.8f, SkyBrightness * 0.9f, SkyBrightness, 0);
        frame.HasHzb = _hasHzb ? 1u : 0u;
        frame.PixelsPerRadian = _targets.Height / (2 * MathF.Tan(camera.FieldOfView * MathF.PI / 360));
        frame.ErrorThreshold = ErrorThresholdPixels;
        frame.FrameIndex = frameIndex;

        return frame;
    }

    /// <summary>
    /// What every view's constants share: its matrices, its targets and the frame's tables.
    /// </summary>
    private FrameConstants GetViewConstants(
        in Matrix4x4 view,
        in Matrix4x4 projection,
        Vector3 cameraPosition,
        float near,
        FrameTargets targets,
        GpuBuffer instances,
        int instanceCount
    )
    {
        Matrix4x4 viewProjection = view * projection;
        Matrix4x4.Invert(viewProjection, out Matrix4x4 inverse);
        FrameConstants frame = new()
        {
            View = view,
            Projection = projection,
            ViewProjection = viewProjection,
            InverseViewProjection = inverse,
            CameraPosition = new Vector4(cameraPosition, 0),
            Width = (uint)targets.Width,
            Height = (uint)targets.Height,
            HzbLevels = (uint)targets.HzbLevels,
            Near = near,
            InstanceCount = (uint)instanceCount,
            MaxVisibleClusters = MaxVisibleClusters,
            DefaultSampler = _sampler.Id,
            Instances = instances.Id,
            Clusters = _pools.Clusters.Id,
            Vertices = _pools.Vertices.Id,
            Indices = _pools.Indices.Id,
            Materials = _materials?.Buffer.Id ?? 0,
            VisibleClusters = _visibleClusters.Id,
            DrawArguments = _drawArguments.Id,
            Counts = _counts.Id,
            RetestList = _retestList.Id,
            RetestArguments = _retestArguments.Id,
            Hzb = targets.Hzb.Id,
            Visibility = targets.Visibility.Id,
            Depth = targets.Depth.Id,
            GBufferNormal = targets.GBufferNormal.Id,
            GBufferBase = targets.GBufferBase.Id,
            GBufferSurface = targets.GBufferSurface.Id,
            Output = targets.Output.Id,
            Captures = _captures.Buffer.Id,
            CaptureSampler = _captureSampler.Id,
        };
        FillFrustum(viewProjection, ref frame);

        return frame;
    }

    private void UploadConstants(GpuBuffer buffer, in FrameConstants frame)
    {
        GpuUpload upload = gpu.BeginUpload(FrameConstants.Bytes);
        MemoryMarshal.Write(upload.Bytes, in frame);
        _graph.Use(buffer, GpuAccess.CopyDestination);
        _graph.Recorder.CopyBuffer(upload.Buffer, upload.Offset, buffer, 0, FrameConstants.Bytes);
    }

    /// <summary>
    /// The counts at zero and the retest dispatch at no groups, in a transfer pass.
    /// </summary>
    private void ResetCounts()
    {
        _graph.Use(_counts, GpuAccess.CopyDestination);
        _graph.Recorder.FillBuffer(_counts, 0);
        GpuUpload retest = gpu.BeginUpload(RetestArgumentsBytes);
        Span<uint> words = MemoryMarshal.Cast<byte, uint>(retest.Bytes);
        words[0] = 0;
        words[1] = 1;
        words[2] = 1;
        _graph.Use(_retestArguments, GpuAccess.CopyDestination);
        _graph.Recorder.CopyBuffer(retest.Buffer, retest.Offset, _retestArguments, 0, RetestArgumentsBytes);
    }

    /// <summary>
    /// The model drawn from every cell's direction into the job's atlases: its finest clusters, at the origin, from
    /// 2 radii away through an orthographic view of its sphere.
    /// </summary>
    private void RecordCapture(in CaptureJob job)
    {
        ResidentModel resident = job.Resident;
        _graph.BeginTransferPass("Capture instance");
        GpuUpload upload = gpu.BeginUpload(GpuInstance.Bytes);
        GpuInstance instance = new()
        {
            World = Matrix4x4.Identity,
            InverseWorld = Matrix4x4.Identity,
            PreviousWorld = Matrix4x4.Identity,
            BoundsCenter = resident.BoundsCenter,
            BoundsRadius = resident.BoundsRadius,
            ClusterOffset = resident.ClusterOffset,
            ClusterCount = resident.ClusterCount,
            Material0 = job.Materials[0],
            Material1 = job.Materials[1],
            Material2 = job.Materials[2],
            Material3 = job.Materials[3],
            Material4 = job.Materials[4],
            Material5 = job.Materials[5],
            Material6 = job.Materials[6],
            Material7 = job.Materials[7],
        };
        MemoryMarshal.Write(upload.Bytes, in instance);
        _graph.Use(_captureInstance, GpuAccess.CopyDestination);
        _graph.Recorder.CopyBuffer(upload.Buffer, upload.Offset, _captureInstance, 0, GpuInstance.Bytes);
        _graph.EndPass();

        float radius = Math.Max(resident.BoundsRadius, 1e-3f);
        for (int cell = 0; cell < CaptureCellCount; cell++)
        {
            Vector2 flat =
                (
                    (new Vector2(cell % GpuCapture.Cells, cell / GpuCapture.Cells) + new Vector2(0.5f))
                    / GpuCapture.Cells
                    * 2
                ) - Vector2.One;
            Vector3 direction = DecodeOctahedral(flat);
            Vector3 cameraPosition = resident.BoundsCenter + (direction * 2 * radius);
            Vector3 up = Math.Abs(direction.Y) > 0.9f ? Vector3.UnitZ : Vector3.UnitY;
            Matrix4x4 view = Matrix4x4.CreateLookAtLeftHanded(Vector3.Zero, -direction, up);
            FrameConstants frame = GetViewConstants(
                view,
                CreateCaptureProjection(radius),
                cameraPosition,
                1e-3f,
                _cellTargets,
                _captureInstance,
                1
            );
            frame.ErrorThreshold = 0;
            frame.PixelsPerRadian = 1;
            frame.Flags = FrameConstants.FlagNoConeCull;

            _graph.BeginTransferPass($"Capture cell {cell} constants");
            UploadConstants(_cellConstants[cell], frame);
            ResetCounts();
            _graph.EndPass();

            RenderView cellView = new(_cellConstants[cell], _captureInstance, 1, _cellTargets);
            RecordCull(cellView, $"Capture cell {cell} cull", mode: 0);
            RecordDraw(cellView, $"Capture cell {cell} draw", TargetLoad.Clear, drawsOffset: 0, countOffset: 0);
            RecordResolve(cellView, $"Capture cell {cell} resolve");

            _graph.BeginComputePass($"Capture cell {cell} write");
            _graph.Use(_cellConstants[cell], GpuAccess.ShaderRead);
            _graph.Use(_captures.Buffer, GpuAccess.ShaderRead);
            _graph.Use(_cellTargets.Visibility, GpuAccess.ShaderRead);
            _graph.Use(_cellTargets.Depth, GpuAccess.ShaderRead);
            _graph.Use(_cellTargets.GBufferNormal, GpuAccess.ShaderRead);
            _graph.Use(_cellTargets.GBufferBase, GpuAccess.ShaderRead);
            _graph.Use(job.Albedo, GpuAccess.ShaderWrite);
            _graph.Use(job.Normal, GpuAccess.ShaderWrite);
            _graph.Use(job.Depth, GpuAccess.ShaderWrite);
            _graph.Recorder.BindPipeline(_pipelines.CaptureCell);
            _graph.Recorder.PushConstants(new GpuConstants(_cellConstants[cell].Id, (uint)cell, job.Row));
            _graph.Recorder.Dispatch(Groups(GpuCapture.CellSize), Groups(GpuCapture.CellSize));
            _graph.EndPass();
        }
    }

    /// <summary>
    /// The cluster cull: mode 0 one group an instance over its clusters, mode 1 the retest list, as many groups as
    /// mode 0 left in the retest arguments.
    /// </summary>
    private void RecordCull(in RenderView view, string name, uint mode)
    {
        _graph.BeginComputePass(name);
        _graph.Use(view.Constants, GpuAccess.ShaderRead);
        _graph.Use(view.Instances, GpuAccess.ShaderRead);
        _graph.Use(_pools.Clusters, GpuAccess.ShaderRead);
        _graph.Use(_counts, GpuAccess.ShaderWrite);
        _graph.Use(_visibleClusters, GpuAccess.ShaderWrite);
        _graph.Use(_drawArguments, GpuAccess.ShaderWrite);
        _graph.Use(view.Targets.Hzb, GpuAccess.ShaderRead);
        _graph.Recorder.BindPipeline(_pipelines.CullClusters);
        _graph.Recorder.PushConstants(new GpuConstants(view.Constants.Id, mode));
        if (mode == 0)
        {
            _graph.Use(_retestList, GpuAccess.ShaderWrite);
            _graph.Use(_retestArguments, GpuAccess.ShaderWrite);
            int rows = (view.InstanceCount + CullGroupsPerRow - 1) / CullGroupsPerRow;
            _graph.Recorder.Dispatch((uint)Math.Min(view.InstanceCount, CullGroupsPerRow), (uint)Math.Max(rows, 1));
        }
        else
        {
            _graph.Use(_retestList, GpuAccess.ShaderRead);
            _graph.Use(_retestArguments, GpuAccess.Indirect);
            _graph.Recorder.DispatchIndirect(_retestArguments, 0);
        }

        _graph.EndPass();
    }

    /// <summary>
    /// The visible clusters drawn into the view's visibility buffer and its depth, one indirect draw each.
    /// </summary>
    private void RecordDraw(in RenderView view, string name, TargetLoad load, ulong drawsOffset, ulong countOffset)
    {
        _graph.BeginDrawPass(
            name,
            new GpuRenderTargets
            {
                Colors = [new GpuColorTarget { Texture = view.Targets.Visibility, Load = load }],
                Depth = new GpuDepthTarget { Texture = view.Targets.Depth, Load = load },
            }
        );
        _graph.Use(view.Constants, GpuAccess.ShaderRead);
        _graph.Use(view.Instances, GpuAccess.ShaderRead);
        _graph.Use(_pools.Vertices, GpuAccess.ShaderRead);
        _graph.Use(_pools.Clusters, GpuAccess.ShaderRead);
        _graph.Use(_pools.Indices, GpuAccess.ShaderRead);
        _graph.Use(_visibleClusters, GpuAccess.ShaderRead);
        _graph.Use(_captures.Buffer, GpuAccess.ShaderRead);
        _captures.UseAll(_graph);
        _graph.Use(_drawArguments, GpuAccess.Indirect);
        _graph.Use(_counts, GpuAccess.Indirect);
        _graph.Recorder.BindPipeline(_pipelines.DrawVisibility);
        _graph.Recorder.PushConstants(new GpuConstants(view.Constants.Id));
        _graph.Recorder.DrawIndirectCount(
            _drawArguments,
            drawsOffset,
            _counts,
            countOffset,
            MaxVisibleClusters,
            DrawArgumentsBytes
        );
        _graph.EndPass();
    }

    /// <summary>
    /// The depth pyramid from the depth so far, a level a pass.
    /// </summary>
    private void RecordHzb()
    {
        for (int level = 0; level < _targets.HzbLevels; level++)
        {
            _graph.BeginComputePass($"Build depth pyramid {level}");
            _graph.Use(_constants, GpuAccess.ShaderRead);
            if (level == 0)
                _graph.Use(_targets.Depth, GpuAccess.ShaderRead);
            else
                _graph.Use(_targets.Hzb, GpuAccess.ShaderRead, level - 1);

            _graph.Use(_targets.Hzb, GpuAccess.ShaderWrite, level);
            _graph.Recorder.BindPipeline(_pipelines.BuildHzb);
            _graph.Recorder.PushConstants(new GpuConstants(_constants.Id, (uint)level));
            int width = Math.Max(_targets.Width >> level, 1);
            int height = Math.Max(_targets.Height >> level, 1);
            _graph.Recorder.Dispatch(Groups(width), Groups(height));
            _graph.EndPass();
        }
    }

    private void RecordResolve(in RenderView view, string name)
    {
        if (_materials is null)
            return;

        _graph.BeginComputePass(name);
        _graph.Use(view.Constants, GpuAccess.ShaderRead);
        _graph.Use(view.Instances, GpuAccess.ShaderRead);
        _graph.Use(_pools.Clusters, GpuAccess.ShaderRead);
        _graph.Use(_pools.Vertices, GpuAccess.ShaderRead);
        _graph.Use(_pools.Indices, GpuAccess.ShaderRead);
        _graph.Use(_materials.Buffer, GpuAccess.ShaderRead);
        _graph.Use(_visibleClusters, GpuAccess.ShaderRead);
        _graph.Use(_captures.Buffer, GpuAccess.ShaderRead);
        _captures.UseAll(_graph);
        _graph.Use(view.Targets.Visibility, GpuAccess.ShaderRead);
        _graph.Use(view.Targets.GBufferNormal, GpuAccess.ShaderWrite);
        _graph.Use(view.Targets.GBufferBase, GpuAccess.ShaderWrite);
        _graph.Use(view.Targets.GBufferSurface, GpuAccess.ShaderWrite);
        _graph.Recorder.BindPipeline(_pipelines.ResolveStandard);
        _graph.Recorder.PushConstants(new GpuConstants(view.Constants.Id));
        _graph.Recorder.Dispatch(Groups(view.Targets.Width), Groups(view.Targets.Height));
        _graph.EndPass();
    }

    private uint Groups(int pixels) => (uint)((pixels + _pipelines.TileSize - 1) / _pipelines.TileSize);

    /// <summary>
    /// Writes the output read back this frame to <paramref name="path"/> once the GPU is done with it.
    /// </summary>
    private async Task SaveScreenshotAsync(string path, int width, int height)
    {
        Result<ReadOnlyMemory<byte>> pixels = await gpu.ReadBackAsync(
                _targets.Readback,
                0,
                width * height * 4,
                CancellationToken.None
            )
            .ConfigureAwait(false);
        if (!pixels.Ok)
        {
            Log.Error($"The screenshot could not be read back: {pixels.Error}");

            return;
        }

        byte[] png = Png.Encode(width, height, pixels.Value.Span);
        Result made = files.CreateDirectory(files.GetParent(path));
        if (!made.Ok)
        {
            Log.Error($"The screenshot's folder could not be made: {made.Error}");

            return;
        }

        Result written = await files.WriteAsync(path, png, CancellationToken.None).ConfigureAwait(false);
        if (written.Ok)
            Log.Info($"Screenshot: {path}");
        else
            Log.Error($"The screenshot could not be written: {written.Error}");
    }

    /// <summary>
    /// One view's buffers and targets: what the passes that draw a view take.
    /// </summary>
    private readonly record struct RenderView(
        GpuBuffer Constants,
        GpuBuffer Instances,
        int InstanceCount,
        FrameTargets Targets
    );
}
