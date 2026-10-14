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
/// The world is copied in LateUpdate (<see cref="Snapshot"/>), and drawn in Render from that copy, one frame behind.
/// While <see cref="IGpu.DebugView"/> names a stage, the frame runs up to it and shows what it made; while the debug UI
/// shows, each instance drawn as the failure says why where it is.
/// </para>
/// </summary>
internal sealed class ClusterRenderingGem : IGem, ICache<ModelField>, ICache<CompressedTexture>, IDisposable
{
    private readonly IGpu _gpu;
    private readonly IDebugUI? _debug;
    private readonly Threads _threads;
    private readonly ClusterRenderingSettings _settings;
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
    private readonly ResourceSet _frame;
    private readonly MeshFields _fields;
    private readonly GiVolume _gi;
    private readonly GpuArray<uint> _zBinsEmpty;
    private readonly GpuArray<uint> _zeros;
    private readonly GpuArray<Vector4> _moved;
    private readonly GpuBuffer _pieceIndices;
    private readonly List<Subscription> _watching = [];
    private readonly List<SurfaceKey> _raster = [];
    private readonly HashSet<GpuTexture> _cleared = [];

    public ClusterRenderingGem(IGpu gpu, IShaderCompiler compiler, Assets assets, Caches caches, World world, Events events, Threads threads, Lifetime lifetime, ClusterRenderingSettings settings, IWindow? window = null, IDebugUI? debug = null)
    {
        _gpu = gpu;
        _debug = debug;
        _threads = threads;
        _settings = settings;
        CancellationToken stopping = lifetime.Stopping;
        _snapshot = new Snapshot(world);
        _textures = new TexturePools(gpu, assets, caches, threads, settings.Anisotropy, stopping);
        _fields = new MeshFields(gpu, assets, caches, settings.GiMeshFields);
        _models = new ClusterPool(gpu, assets, threads, _textures, _fields, settings, stopping);
        _materials = new Materials(gpu, assets, _textures, threads, stopping);
        _scene = new Scene(gpu, assets, _models, _materials);
        _lights = new SceneLights(gpu);
        _shadows = new Shadows(gpu);
        Handle<Pipeline> pipeline = settings.Pipeline == Handle<Pipeline>.None ? assets.Default<Pipeline>() : settings.Pipeline;
        _graph = new PassGraph(gpu, compiler, assets, pipeline, threads, stopping);
        _pieceIndices = gpu.CreateBuffer(GpuBufferUsage.Index, PassRecorder.PieceIndexCount * sizeof(ushort));
        ushort[] indices = new ushort[PassRecorder.PieceIndexCount];
        for (int i = 0; i < indices.Length; i++)
        {
            indices[i] = (ushort)i;
        }

        gpu.Upload(_pieceIndices, 0, MemoryMarshal.AsBytes(indices.AsSpan()));
        _recorder = new PassRecorder(gpu, _graph, _scene, _models, _materials, _textures, _pieceIndices);
        _views = new Views(gpu, window, assets, threads, settings, stopping);
        _output = new Output(gpu, compiler, assets, threads, stopping);
        _frame = new ResourceSet(gpu);
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
    }

    public void Dispose()
    {
        foreach (Subscription watch in _watching)
        {
            watch.Dispose();
        }

        _snapshot.Dispose();
        _frame.Dispose();
        _gi.Dispose();
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
        _gpu.Release(_pieceIndices);
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
    /// Takes in what changed, places the sun's shadow about the main camera, then for every camera with somewhere to draw
    /// records the frame's stages once (probes), each view's stages, and the view shown in its target: up to the stage
    /// the debug view names, and what that stage made shown in place of the finished image. A window nothing is drawn
    /// into, with no camera on it or while the frame's passes first compile, is cleared to black.
    /// </summary>
    public void Render(in Frame frame)
    {
        CameraCopy? main = MainCamera();
        Vector3D? player = _snapshot.Player ?? main?.Transform.Position;
        _scene.Update(_snapshot, (float)frame.Seconds, player ?? default);
        _lights.Update(_snapshot);
        if (main is { } camera)
        {
            _shadows.Plan(camera.Transform.Position, _snapshot.Sun?.Direction ?? -Vector3.UnitY, _lights.Changed, _lights.Capacity, _scene.Moved.Count, frame.Index, _settings);
        }

        ReportFailures();
        _models.Flush();
        _materials.Flush();
        DrawnMaterial? sky = _snapshot.Sky is { } material && material != Handle<Material>.None ? _materials.Get(material) : null;
        SurfaceKey skySurface = sky is { } drawnSky ? drawnSky.Surface : default;
        _raster.Clear();
        _raster.Add(new SurfaceKey(Handle<Shader>.None, false, false));
        _raster.Add(new SurfaceKey(Handle<Shader>.None, false, true));
        _raster.AddRange(_materials.RasterBins);
        _graph.Update(_snapshot.Effects, new SceneVariants(_raster, _materials.ResolveBins, _materials.TransparentSurfaces, skySurface));
        if (_graph.Current?.Runs("GiField") == true && main is { } eye)
        {
            _gi.Plan(eye.Transform.Position, _scene.Moved, _settings);
        }
        else
        {
            _gi.Clear();
        }

        Counts counts = new(
            Instances: (uint)_scene.Capacity,
            Pieces: (uint)Math.Max(_settings.PieceCapacity, 1024),
            Clusters: (uint)Math.Max(_settings.PieceCapacity, 1024),
            // Room for every instance twice over, so the shadow page that holds the whole world always fits.
            Jobs: (uint)Math.Max(Math.Max(_settings.PieceCapacity / 16, 2 * _scene.Capacity), 1024),
            RasterBins: Materials.RasterBinRows,
            ResolveBins: (uint)Math.Max(_materials.ResolveBins.Count, 1),
            Tiles: 0,
            HizTexels: 0,
            HizLevels: 0,
            Lights: BitOperations.RoundUpToPowerOf2((uint)Math.Clamp(_settings.LightCapacity, 32, 1 << 20)),
            ZBins: Views.ZBinCount,
            LightTiles: 0,
            LightTileWords: 0,
            Moved: (uint)_scene.Moved.Count,
            GiRays: _gi.Rays,
            GiProbeThreads: _gi.ProbeThreads,
            SceneLights: (uint)_lights.Capacity,
            SunEntries: (uint)_shadows.SunEntries,
            LightEntries: (uint)_shadows.LightEntries,
            ShadowViews: (uint)Math.Max(_settings.ShadowUpdates, 1),
            ShadowGroups: (uint)(Math.Max(_settings.ShadowUpdates, 1) + _settings.ShadowLevels),
            ShadowCandidates: (uint)(_shadows.WholePages + _shadows.SmallPages));
        List<View> views = _views.Prepare(new ViewFrame(_snapshot, frame, _scene, _models, _materials, sky?.Row ?? 0, counts, player, Jitters()));
        _cleared.Clear();
        if (_graph.Current is not { } listing || views.Count == 0)
        {
            ClearWindow();
            _textures.EndFrame();
            return;
        }

        View shown = views.Find(view => view.TargetIsWindow) ?? views[0];
        SetFrame(main?.Transform.Position ?? shown.Position);
        PassStage? debugView = _gpu.DebugView;
        PassStage last = debugView ?? PassStage.Ui;
        RecordScope frameScope = new(_frame, null, shown.Constants, shown.Counts, _materials.RasterBins, _materials.ResolveBins, [], GpuBuffer.None, skySurface, []);
        for (PassStage stage = PassStage.Shadows; stage < PassStage.Cull && stage <= last; stage++)
        {
            _recorder.Record(listing, stage, frameScope);
        }

        foreach (View view in views)
        {
            view.Resources.Keep(listing);
            RecordScope scope = new(_frame, view.Resources, view.Constants, view.Counts, _materials.RasterBins, _materials.ResolveBins, view.TransparentRuns, view.TransparentPieces, skySurface, []);
            for (PassStage stage = PassStage.Cull; stage <= last; stage++)
            {
                _recorder.Record(listing, stage, scope);
            }

            BoundResource image = debugView is { } debugged
                ? DebugImage(listing, view, debugged)
                : view.Resources.TryGet("Ldr", out BoundResource ldr) ? ldr : view.Resources.TryGet("Hdr", out BoundResource hdr) ? hdr : default;
            _output.Draw(view.Target, view.Constants, image, _cleared.Add(view.Target.Texture));
            view.Resources.Swap();
        }

        _frame.Keep(listing);
        ClearWindow();
        _textures.EndFrame();
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
    /// The frame's names this frame: the scene's tables, as they may have grown, where it moved relative to the main
    /// camera, which the shadows are placed about, the templates binning and counting start from, and the shadows' pool
    /// and tables.
    /// </summary>
    private void SetFrame(Vector3D main)
    {
        for (int i = 0; i < _scene.Moved.Count; i++)
        {
            MovedSphere sphere = _scene.Moved[i];
            _moved.Set(i, new Vector4((sphere.Centre - main).ToVector3(), sphere.Radius));
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
        _frame.Set("Moved", BoundResource.Of(_moved.Buffer, _moved.Bytes));
        _frame.Set("ZBinsEmpty", BoundResource.Of(_zBinsEmpty.Buffer, _zBinsEmpty.Bytes));
        _frame.Set("Zeros", BoundResource.Of(_zeros.Buffer, _zeros.Bytes));
        _frame.Set("MeshFieldAtlas", BoundResource.Of(_fields.Atlas, MeshFields.Resolution, MeshFields.Resolution));
        _frame.Set("MeshFields", BoundResource.Of(_fields.Table.Buffer, _fields.Table.Bytes));
        _frame.Set("MaterialGi", BoundResource.Of(_materials.Gi.Buffer, _materials.Gi.Bytes));
        _frame.Set("GiLevels", BoundResource.Of(_gi.Rows.Buffer, _gi.Rows.Bytes));
        _frame.Set("SceneLights", BoundResource.Of(_lights.Rows.Buffer, _lights.Rows.Bytes));
        _shadows.Bind(_frame);
    }

    /// <summary>
    /// Whether views are drawn a sub-pixel off each frame, which the temporal upscale resolves into detail finer than a
    /// pixel: only while the frame runs it, as nothing else would undo the shake.
    /// </summary>
    private bool Jitters() => _graph.Current?.Runs("Taa") == true;

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
            debug.Error(why, _scene.PositionOf(slot).ToVector3(), 0, what);
        }
    }

    /// <summary>
    /// What <paramref name="view"/> shows in place of its finished image while the frame is viewed at
    /// <paramref name="stage"/>: the first 2D texture that the stage's last pass to make one draws into or writes, so
    /// the G-buffer stage shows its first target and the lighting the scene colour; the scene colour when the stage
    /// makes none, as one that only fills buffers.
    /// </summary>
    private BoundResource DebugImage(Listing listing, View view, PassStage stage)
    {
        List<ListedPass> passes = listing.Stages[(int)stage];
        for (int i = passes.Count - 1; i >= 0; i--)
        {
            Pass pass = passes[i].Pass;
            if (pass.Kind == PassKind.Draw)
            {
                foreach (string color in pass.Draw.Colors)
                {
                    if (TextureOf(listing, view, color) is { } drawn)
                    {
                        return drawn;
                    }
                }

                if (TextureOf(listing, view, pass.Draw.Depth) is { } depth)
                {
                    return depth;
                }

                continue;
            }

            foreach (PassBinding write in passes[i].TextureWrites)
            {
                if (TextureOf(listing, view, write.Info.Name) is { } written)
                {
                    return written;
                }
            }
        }

        return view.Resources.TryGet("Hdr", out BoundResource hdr) ? hdr : default;
    }

    /// <summary>
    /// The texture <paramref name="name"/> names for <paramref name="view"/> or the frame, when it is a 2D texture that
    /// is there; none for a buffer, an array, a cube, a volume or a texture that cannot be sampled, which a view's image
    /// cannot be.
    /// </summary>
    private BoundResource? TextureOf(Listing listing, View view, string name)
    {
        ResourceInfo? info = listing.Created.TryGetValue(name, out CreatedResource? created) ? created.Info : ResourceInfo.Engine.GetValueOrDefault(name);
        if (info is not { IsBuffer: false, IsStorageRead: false, Shape: GpuTextureKind.Texture2D })
        {
            return null;
        }

        return (info.IsFrame ? _frame : view.Resources).TryGet(name, out BoundResource bound) && bound.Texture != GpuTexture.None ? bound : null;
    }

    /// <summary>
    /// The camera drawing into the window, else the first; none without a camera.
    /// </summary>
    private CameraCopy? MainCamera()
    {
        foreach (CameraCopy camera in _snapshot.Cameras)
        {
            if (camera.Camera.Target.IsWindow)
            {
                return camera;
            }
        }

        return _snapshot.Cameras.Count > 0 ? _snapshot.Cameras[0] : null;
    }

    private void OnRender(Action work)
    {
        _threads.Post(EngineThread.Render, work);
    }
}
