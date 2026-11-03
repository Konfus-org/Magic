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
/// <c>Render</c> uploads the frame's tables, culls every resident model's clusters on the GPU against the frustum,
/// their cones, the pixel error threshold and last frame's depth pyramid, draws the visible ones into a visibility
/// buffer, retests what the old pyramid hid against the new one, resolves every pixel's material into the GBuffer,
/// lights it with the sun, shows it in the window and saves it when a screenshot is asked for. It is public because
/// nothing in its assembly makes it: Magic does, by reflection, when a project lists the gem.
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
    private const int MaxInstances = 1 << 16;
    private const int MaxVisibleClusters = 1 << 18;
    private const int CountsBytes = 64;
    private const int DrawArgumentsBytes = 16;
    private const int RetestArgumentsBytes = 12;
    private const int DefaultWidth = 1280;
    private const int DefaultHeight = 720;
    private const float ErrorThresholdPixels = 1;
    private const float SkyBrightness = 0.3f;

    private readonly RenderGraph _graph = new(gpu);
    private readonly RenderWorld _world = new();
    private readonly GeometryPools _pools = new(gpu, meshes, assets, threads);
    private readonly MaterialTable _materials = new(gpu, assets, threads);
    private readonly FrameTargets _targets = new(gpu);
    private readonly RendererPipelines _pipelines = new(gpu, shaders, assets);
    private GpuBuffer _constants;
    private GpuBuffer _instances;
    private GpuBuffer _counts;
    private GpuBuffer _retestArguments;
    private GpuBuffer _visibleClusters;
    private GpuBuffer _drawArguments;
    private GpuBuffer _retestList;
    private GpuSampler _sampler;
    private bool _isReady;
    private bool _hasHzb;

    public void Dispose()
    {
        _graph.Clear();
        _pipelines.Dispose();
        _targets.Dispose();
        _materials.Dispose();
        _pools.Dispose();
        foreach (
            GpuBuffer buffer in (ReadOnlySpan<GpuBuffer>)
                [_constants, _instances, _counts, _retestArguments, _visibleClusters, _drawArguments, _retestList]
        )
        {
            if (!buffer.IsNone)
                gpu.Destroy(buffer);
        }

        if (!_sampler.IsNone)
            gpu.Destroy(_sampler);
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

        Result made = _pipelines.Create(compiled.Value);
        if (made.Ok)
            made = _pools.Create();

        if (made.Ok)
            made = _materials.Create();

        if (made.Ok)
            made = CreateBuffers();

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
                    _world.Instances.Add(new RenderInstance(renderer.Model, renderer.Materials, chunk.First[i]));
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
                float exposure = light.Intensity > 0 ? 1 / light.Intensity : 0;
                Vector3 radiance =
                    new Vector3(light.Color.R, light.Color.G, light.Color.B) * light.Intensity * exposure;
                _world.Sun = new RenderSun(towards, radiance);
            }
        }

        return ValueTask.FromResult(state);
    }

    /// <summary>
    /// Draws the frame and submits it.
    /// </summary>
    public ValueTask<Render> RunAsync(Render state, CancellationToken cancel)
    {
        if (!_isReady)
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

        // The upload: what arrived in the pools, the tables, the constants and the counts reset.
        _graph.BeginTransferPass("Upload");
        _pools.Upload(_graph);
        _materials.Upload(_graph);
        int instanceCount = UploadInstances();
        UploadConstants(state.Frame.Index, instanceCount);
        _graph.Use(_counts, GpuAccess.CopyDestination);
        _graph.Recorder.FillBuffer(_counts, 0);
        GpuUpload retest = gpu.BeginUpload(RetestArgumentsBytes);
        MemoryMarshal.Cast<byte, uint>(retest.Bytes)[0] = 0;
        MemoryMarshal.Cast<byte, uint>(retest.Bytes)[1] = 1;
        MemoryMarshal.Cast<byte, uint>(retest.Bytes)[2] = 1;
        _graph.Use(_retestArguments, GpuAccess.CopyDestination);
        _graph.Recorder.CopyBuffer(retest.Buffer, retest.Offset, _retestArguments, 0, RetestArgumentsBytes);
        _graph.EndPass();

        RecordCull("Cull clusters", mode: 0, instanceCount);
        RecordDraw("Draw visibility", TargetLoad.Clear, drawsOffset: 0, countOffset: 0);
        RecordHzb();
        RecordCull("Retest clusters", mode: 1, instanceCount);
        RecordDraw(
            "Draw retested",
            TargetLoad.Keep,
            drawsOffset: (ulong)MaxVisibleClusters * DrawArgumentsBytes,
            countOffset: 8
        );
        RecordHzb();
        RecordResolve();
        RecordShade();

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
    /// scaled so its normal is a unit.
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
        frame.FrustumPlane5 = new Vector4(0, 0, 0, 1);
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
    private static Matrix4x4 CreateProjection(float fieldOfViewDegrees, float aspect, float near)
    {
        float focal = 1 / MathF.Tan(fieldOfViewDegrees * MathF.PI / 360);
        Matrix4x4 projection = default;
        projection.M11 = focal / aspect;
        projection.M22 = focal;
        projection.M34 = 1;
        projection.M43 = near;

        return projection;
    }

    private Result CreateBuffers()
    {
        Result<GpuSampler> sampler = gpu.CreateSampler(new SamplerDescription { Anisotropy = 8 });
        if (!sampler.Ok)
            return Result.Failure($"the default sampler was not made: {sampler.Error}");

        _sampler = sampler.Value;
        (string Name, long Bytes, BufferUsages Usage)[] descriptions =
        [
            ("Frame constants", FrameConstants.Bytes, BufferUsages.Storage),
            ("Instance table", (long)MaxInstances * GpuInstance.Bytes, BufferUsages.Storage),
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

        (_constants, _instances, _counts, _retestArguments, _visibleClusters, _drawArguments, _retestList) = (
            made[0],
            made[1],
            made[2],
            made[3],
            made[4],
            made[5],
            made[6]
        );

        return Result.Success();
    }

    /// <summary>
    /// The instance table for the frame: every resident instance, relative to the camera; how many there are.
    /// </summary>
    private int UploadInstances()
    {
        Vector3 cameraPosition = _world.Camera?.Transform.Position ?? Vector3.Zero;
        int count = Math.Min(_world.Instances.Count, MaxInstances);
        if (_world.Instances.Count > MaxInstances)
            Log.Warn($"{_world.Instances.Count - MaxInstances} renderers are not drawn: the instance table is full.");

        GpuUpload upload = gpu.BeginUpload(Math.Max(count, 1) * GpuInstance.Bytes);
        Span<GpuInstance> rows = MemoryMarshal.Cast<byte, GpuInstance>(upload.Bytes);
        int written = 0;
        for (int i = 0; i < count; i++)
        {
            RenderInstance instance = _world.Instances[i];
            if (_pools.Get(instance.Model) is not { } resident)
                continue;

            Transform relative = instance.Transform;
            relative.Position -= cameraPosition;
            Matrix4x4 world = relative.Matrix;
            float scale = Math.Max(
                Math.Abs(relative.Scale.X),
                Math.Max(Math.Abs(relative.Scale.Y), Math.Abs(relative.Scale.Z))
            );
            MaterialSlots materials = instance.Materials;
            rows[written++] = new GpuInstance
            {
                World = world,
                PreviousWorld = world,
                BoundsCenter = Vector3.Transform(resident.BoundsCenter, world),
                BoundsRadius = resident.BoundsRadius * scale,
                ClusterOffset = resident.ClusterOffset,
                ClusterCount = resident.ClusterCount,
                Material0 = _materials.GetRow(materials[0]),
                Material1 = _materials.GetRow(materials[1]),
                Material2 = _materials.GetRow(materials[2]),
                Material3 = _materials.GetRow(materials[3]),
                Material4 = _materials.GetRow(materials[4]),
                Material5 = _materials.GetRow(materials[5]),
                Material6 = _materials.GetRow(materials[6]),
                Material7 = _materials.GetRow(materials[7]),
            };
        }

        if (written > 0)
        {
            _graph.Use(_instances, GpuAccess.CopyDestination);
            _graph.Recorder.CopyBuffer(upload.Buffer, upload.Offset, _instances, 0, (ulong)written * GpuInstance.Bytes);
        }

        return written;
    }

    private void UploadConstants(uint frameIndex, int instanceCount)
    {
        float aspect = (float)_targets.Width / _targets.Height;
        RenderCamera camera = _world.Camera ?? new RenderCamera(new Transform(), 60, 0.05f);
        Matrix4x4.Invert(Matrix4x4.CreateFromQuaternion(camera.Transform.Rotation), out Matrix4x4 view);
        Matrix4x4 projection = CreateProjection(camera.FieldOfView, aspect, camera.Near);
        Matrix4x4 viewProjection = view * projection;
        Matrix4x4.Invert(viewProjection, out Matrix4x4 inverse);
        RenderSun sun = _world.Sun ?? new RenderSun(Vector3.Normalize(new Vector3(0.3f, 0.8f, -0.5f)), Vector3.One);
        FrameConstants frame = new()
        {
            View = view,
            Projection = projection,
            ViewProjection = viewProjection,
            InverseViewProjection = inverse,
            SunDirection = new Vector4(sun.Direction, 0),
            SunRadiance = new Vector4(sun.Radiance, 0),
            SkyRadiance = new Vector4(SkyBrightness * 0.8f, SkyBrightness * 0.9f, SkyBrightness, 0),
            Width = (uint)_targets.Width,
            Height = (uint)_targets.Height,
            HzbLevels = (uint)_targets.HzbLevels,
            HasHzb = _hasHzb ? 1u : 0u,
            Near = camera.Near,
            PixelsPerRadian = _targets.Height / (2 * MathF.Tan(camera.FieldOfView * MathF.PI / 360)),
            ErrorThreshold = ErrorThresholdPixels,
            InstanceCount = (uint)instanceCount,
            MaxVisibleClusters = MaxVisibleClusters,
            FrameIndex = frameIndex,
            DefaultSampler = _sampler.Id,
            Instances = _instances.Id,
            Clusters = _pools.Clusters.Id,
            Vertices = _pools.Vertices.Id,
            Indices = _pools.Indices.Id,
            Materials = _materials.Buffer.Id,
            VisibleClusters = _visibleClusters.Id,
            DrawArguments = _drawArguments.Id,
            Counts = _counts.Id,
            RetestList = _retestList.Id,
            RetestArguments = _retestArguments.Id,
            Hzb = _targets.Hzb.Id,
            Visibility = _targets.Visibility.Id,
            Depth = _targets.Depth.Id,
            GBufferNormal = _targets.GBufferNormal.Id,
            GBufferBase = _targets.GBufferBase.Id,
            GBufferSurface = _targets.GBufferSurface.Id,
            Output = _targets.Output.Id,
        };
        FillFrustum(viewProjection, ref frame);
        GpuUpload upload = gpu.BeginUpload(FrameConstants.Bytes);
        MemoryMarshal.Write(upload.Bytes, in frame);
        _graph.Use(_constants, GpuAccess.CopyDestination);
        _graph.Recorder.CopyBuffer(upload.Buffer, upload.Offset, _constants, 0, FrameConstants.Bytes);
    }

    /// <summary>
    /// The cluster cull: mode 0 one group an instance over its clusters, mode 1 the retest list, as many groups as
    /// mode 0 left in the retest arguments.
    /// </summary>
    private void RecordCull(string name, uint mode, int instanceCount)
    {
        _graph.BeginComputePass(name);
        _graph.Use(_constants, GpuAccess.ShaderRead);
        _graph.Use(_instances, GpuAccess.ShaderRead);
        _graph.Use(_pools.Clusters, GpuAccess.ShaderRead);
        _graph.Use(_counts, GpuAccess.ShaderWrite);
        _graph.Use(_visibleClusters, GpuAccess.ShaderWrite);
        _graph.Use(_drawArguments, GpuAccess.ShaderWrite);
        _graph.Use(_targets.Hzb, GpuAccess.ShaderRead);
        _graph.Recorder.BindPipeline(_pipelines.CullClusters);
        _graph.Recorder.PushConstants(new GpuConstants(_constants.Id, mode));
        if (mode == 0)
        {
            _graph.Use(_retestList, GpuAccess.ShaderWrite);
            _graph.Use(_retestArguments, GpuAccess.ShaderWrite);
            _graph.Recorder.Dispatch((uint)instanceCount);
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
    /// The visible clusters drawn into the visibility buffer and its depth, one indirect draw each.
    /// </summary>
    private void RecordDraw(string name, TargetLoad load, ulong drawsOffset, ulong countOffset)
    {
        _graph.BeginDrawPass(
            name,
            new GpuRenderTargets
            {
                Colors = [new GpuColorTarget { Texture = _targets.Visibility, Load = load }],
                Depth = new GpuDepthTarget { Texture = _targets.Depth, Load = load },
            }
        );
        _graph.Use(_constants, GpuAccess.ShaderRead);
        _graph.Use(_instances, GpuAccess.ShaderRead);
        _graph.Use(_pools.Vertices, GpuAccess.ShaderRead);
        _graph.Use(_pools.Clusters, GpuAccess.ShaderRead);
        _graph.Use(_pools.Indices, GpuAccess.ShaderRead);
        _graph.Use(_visibleClusters, GpuAccess.ShaderRead);
        _graph.Use(_drawArguments, GpuAccess.Indirect);
        _graph.Use(_counts, GpuAccess.Indirect);
        _graph.Recorder.BindPipeline(_pipelines.DrawVisibility);
        _graph.Recorder.PushConstants(new GpuConstants(_constants.Id));
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

    private void RecordResolve()
    {
        _graph.BeginComputePass("Resolve materials");
        _graph.Use(_constants, GpuAccess.ShaderRead);
        _graph.Use(_instances, GpuAccess.ShaderRead);
        _graph.Use(_pools.Clusters, GpuAccess.ShaderRead);
        _graph.Use(_pools.Vertices, GpuAccess.ShaderRead);
        _graph.Use(_pools.Indices, GpuAccess.ShaderRead);
        _graph.Use(_materials.Buffer, GpuAccess.ShaderRead);
        _graph.Use(_visibleClusters, GpuAccess.ShaderRead);
        _graph.Use(_targets.Visibility, GpuAccess.ShaderRead);
        _graph.Use(_targets.GBufferNormal, GpuAccess.ShaderWrite);
        _graph.Use(_targets.GBufferBase, GpuAccess.ShaderWrite);
        _graph.Use(_targets.GBufferSurface, GpuAccess.ShaderWrite);
        _graph.Recorder.BindPipeline(_pipelines.ResolveStandard);
        _graph.Recorder.PushConstants(new GpuConstants(_constants.Id));
        _graph.Recorder.Dispatch(Groups(_targets.Width), Groups(_targets.Height));
        _graph.EndPass();
    }

    private void RecordShade()
    {
        _graph.BeginComputePass("Shade");
        _graph.Use(_constants, GpuAccess.ShaderRead);
        _graph.Use(_targets.Visibility, GpuAccess.ShaderRead);
        _graph.Use(_targets.GBufferNormal, GpuAccess.ShaderRead);
        _graph.Use(_targets.GBufferBase, GpuAccess.ShaderRead);
        _graph.Use(_targets.GBufferSurface, GpuAccess.ShaderRead);
        _graph.Use(_targets.Output, GpuAccess.ShaderWrite);
        _graph.Recorder.BindPipeline(_pipelines.ShadeFlat);
        _graph.Recorder.PushConstants(new GpuConstants(_constants.Id));
        _graph.Recorder.Dispatch(Groups(_targets.Width), Groups(_targets.Height));
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
}
