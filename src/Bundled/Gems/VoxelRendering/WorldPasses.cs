using System.Drawing;
using System.Runtime.InteropServices;
using Magic.Contexts.Assets;
using Magic.Contexts.Gpu;
using Magic.Contexts.Rendering;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.VoxelRendering;

/// <summary>
/// How much of the world a view's culling buffers hold: instances, pieces of up to 32 triangles, visible clusters, the
/// jobs clusters are culled in, and the resolve bins.
/// </summary>
internal readonly record struct WorldCapacity(uint Instances, uint Pieces, uint Clusters, uint Jobs, uint ResolveBins);

/// <summary>
/// The renderer's steps for the world (<see cref="IRendering.Cull"/>, <see cref="IRendering.DrawVisibility"/>,
/// <see cref="IRendering.BuildDepthPyramid"/>, <see cref="IRendering.Classify"/>, <see cref="IRendering.Resolve"/>):
/// each view culled in two phases (Shaders/Renderer/Culling.slang) into buffers of its own, kept from frame to frame
/// and made again when the view's size or the world's capacity changes; what it found drawn into a visibility buffer,
/// each raster bin with its surface's pipeline, a piece's draw indexing its 96 corners; the depth pyramid the next
/// culling tests against; and each pixel's surface resolved over the tiles that show it. A view no frame drew is let go
/// as the frame ends. Render thread.
/// </summary>
internal sealed class WorldPasses : IDisposable
{
    /// <summary>
    /// Indices a piece's draw has, its 32 triangles' corners in order, which its vertex stage works its corner out
    /// from.
    /// </summary>
    private const int PieceIndices = 96;

    // Bytes of an indexed draw's arguments, and of a dispatch's.
    private const uint DrawBytes = 20;
    private const uint DispatchBytes = 12;

    // The culling passes' phases (Culling.slang).
    private const uint FirstPhase = 1;
    private const uint SecondPhase = 2;

    // The culling buffers' rows: counts, the last the groups done, the distance buckets, the raster bins.
    private const uint Counts = 9;
    private const uint Buckets = 64;
    private const uint RasterBins = GpuMaterials.RasterBinRows;

    /// <summary>
    /// Resolve bins at most, and so rows of the resolve's draws; the tile counts have one more, the groups done.
    /// </summary>
    private const uint ResolveRows = 256;

    private const GpuBufferUsage Usage = GpuBufferUsage.GraphicsRead
        | GpuBufferUsage.ComputeRead
        | GpuBufferUsage.ComputeWrite
        | GpuBufferUsage.Arguments;

    private readonly IGpuDriver _gpu;
    private readonly Passes _passes;
    private readonly GpuPipelines _pipelines;
    private readonly GpuMaterials _materials;
    private readonly GpuBuffer _failures;
    private readonly Dictionary<int, ViewBuffers> _views = [];
    private readonly HashSet<int> _drawn = [];
    private readonly List<int> _gone = [];
    private readonly ShaderEntry _beginCulling;
    private readonly ShaderEntry _cullInstances;
    private readonly ShaderEntry _cullClusters;
    private readonly ShaderEntry _writePieces;
    private readonly ShaderEntry _buildDepthPyramid;
    private readonly ShaderEntry _classifyTiles;

    /// <summary>
    /// A piece's 96 corners, 0 to 95, which every draw of the world's materials indexes.
    /// </summary>
    private GpuBuffer _indices;

    private WorldCapacity _capacity;

    internal WorldPasses(
        IGpuDriver gpu,
        Passes passes,
        GpuPipelines pipelines,
        GpuMaterials materials,
        GpuBuffer failures,
        Assets assets)
    {
        _gpu = gpu;
        _passes = passes;
        _pipelines = pipelines;
        _materials = materials;
        _failures = failures;
        Handle<Shader> culling = assets.Find<Shader>("Shaders/Renderer/Culling.slang");
        Handle<Shader> depthPyramid = assets.Find<Shader>("Shaders/Renderer/DepthPyramid.slang");
        Handle<Shader> materialsModule = assets.Find<Shader>("Shaders/Renderer/Materials.slang");
        _beginCulling = new ShaderEntry(culling, "BeginCulling");
        _cullInstances = new ShaderEntry(culling, "CullInstances");
        _cullClusters = new ShaderEntry(culling, "CullClusters");
        _writePieces = new ShaderEntry(culling, "WritePieces");
        _buildDepthPyramid = new ShaderEntry(depthPyramid, "BuildDepthPyramid");
        _classifyTiles = new ShaderEntry(materialsModule, "ClassifyTiles");
    }

    public void Dispose()
    {
        foreach (ViewBuffers view in _views.Values)
        {
            view.Release(_gpu);
        }

        _gpu.Release(_indices);
    }

    /// <summary>
    /// Starts a frame whose views' buffers hold <paramref name="capacity"/>.
    /// </summary>
    internal void BeginFrame(WorldCapacity capacity)
    {
        _capacity = capacity;
        _drawn.Clear();
    }

    /// <summary>
    /// Ends the frame: the buffers of each view it did not draw are let go.
    /// </summary>
    internal void EndFrame()
    {
        _gone.Clear();
        foreach (int id in _views.Keys)
        {
            if (!_drawn.Contains(id)) _gone.Add(id);
        }

        foreach (int id in _gone)
        {
            _views[id].Release(_gpu);
            _views.Remove(id);
        }
    }

    /// <summary>
    /// Culls a phase: the first starts the view's culling and tests every instance, the second those the first hid;
    /// then their clusters, the second phase's with the clusters the first hid; then the phase's pieces placed. Each
    /// pass's last group writes the groups the next runs.
    /// </summary>
    internal VisibleWorld Cull(RenderView view, CullPhase phase)
    {
        ViewBuffers b = GetBuffers(view);
        bool isFirst = phase == CullPhase.First;
        Culling c = b.ToCulling(isFirst ? FirstPhase : SecondPhase);
        uint row = isFirst ? 0u : 1u;
        if (isFirst)
        {
            _passes.Dispatch(view, _beginCulling, c, new GpuGroups(1), [], [b.Budget, b.Counts]);
            _passes.Dispatch(
                view,
                _cullInstances,
                c,
                GpuGroups.For(Math.Max(_capacity.Instances, 1), 64),
                [b.HiZ],
                [
                    b.Counts, b.Clusters, b.BinCounts, b.Budget, b.Jobs, b.HiddenInstances, b.InstanceArgs,
                    b.ClusterArgs,
                ]);
        }
        else
        {
            _passes.Dispatch(
                view,
                _cullInstances,
                c,
                b.InstanceArgs,
                0,
                [b.HiddenInstances, b.HiZ],
                [b.Counts, b.Clusters, b.BinCounts, b.Budget, b.Jobs, b.ClusterArgs]);
        }

        _passes.Dispatch(
            view,
            _cullClusters,
            c,
            b.ClusterArgs,
            row * DispatchBytes,
            [b.Jobs, b.HiZ],
            [b.Counts, b.Clusters, b.HiddenClusters, b.BinCounts, b.BinCursors, b.BinArgs, b.Budget, b.PieceArgs]);
        _passes.Dispatch(
            view,
            _writePieces,
            c,
            b.PieceArgs,
            row * DispatchBytes,
            [b.Counts, b.Clusters, b.BinArgs],
            [b.BinCursors, b.Pieces]);

        return new VisibleWorld(phase, b.Pieces, b.BinArgs, row * RasterBins);
    }

    /// <summary>
    /// Draws what a culling found: the bins that do not clip, single- then double-sided; then each surface's own, its
    /// clusters' and its cards'.
    /// </summary>
    internal void DrawVisibility(
        RenderView view,
        in VisibleWorld visible,
        GpuTexture ids,
        GpuTexture depth,
        GpuContents contents)
    {
        TargetFormats formats = new(
            _gpu.GetFormat(ids),
            GpuFormat.Invalid,
            GpuFormat.Invalid,
            GpuFormat.Invalid,
            GpuFormat.Invalid,
            _gpu.GetFormat(depth));
        PassConstants<MaterialConstants> constants = new(
            _passes.GetHeader(view),
            new MaterialConstants(visible.Pieces, GpuBuffer.None, GpuTexture.None));
        using GpuDebugGroup group = _gpu.Commands.DebugGroup("Visibility");
        using GpuRenderPass render = _gpu.Commands.RenderPass(
            [new GpuTextureLevel(ids)],
            contents,
            depth: new GpuTextureLevel(depth));
        render.Reads([visible.Pieces]).IndexBuffer(GetIndices(), GpuIndexSize.Bits32);
        SurfaceKey singleSided = new(Handle<Shader>.None, false, false);
        SurfaceKey doubleSided = new(Handle<Shader>.None, false, true);
        DrawBin(render, MaterialPass.Visibility, singleSided, formats, constants, visible.Draws, visible.FirstDraw);
        DrawBin(render, MaterialPass.Visibility, doubleSided, formats, constants, visible.Draws, visible.FirstDraw + 1);
        for (int i = 0; i < _materials.RasterBins.Count; i++)
        {
            uint bin = visible.FirstDraw + GpuMaterials.FixedRasterBins + (uint)i;
            uint cards = bin + GpuMaterials.CardBinOffset;
            SurfaceKey surface = _materials.RasterBins[i];
            DrawBin(render, MaterialPass.Visibility, surface, formats, constants, visible.Draws, bin);
            DrawBin(render, MaterialPass.Visibility, surface, formats, constants, visible.Draws, cards);
        }
    }

    /// <summary>
    /// The depth pyramid of what the view drew so far, every level in one dispatch, a group to 32 x 32 texels of its
    /// first level.
    /// </summary>
    internal void BuildDepthPyramid(RenderView view, GpuTexture depth)
    {
        ViewBuffers b = GetBuffers(view);
        Size first = new(Math.Max((view.Size.Width + 1) / 2, 1), Math.Max((view.Size.Height + 1) / 2, 1));
        _passes.Dispatch(
            view,
            _buildDepthPyramid,
            new DepthPyramidConstants(depth, b.HiZ, b.Counts, b.HiZLevels),
            GpuGroups.For(first, 32),
            [depth],
            [b.HiZ, b.Counts]);
    }

    internal SurfaceTiles Classify(RenderView view, GpuTexture ids)
    {
        ViewBuffers b = GetBuffers(view);
        _passes.Dispatch(
            view,
            _classifyTiles,
            new ClassifyConstants(ids, b.Pieces, b.TileCounts, b.TileList, b.ResolveArgs),
            GpuGroups.For(view.Size, 8),
            [ids, b.Pieces],
            [b.TileCounts, b.TileList, b.ResolveArgs, _failures]);

        return new SurfaceTiles(b.Pieces, b.TileList, b.ResolveArgs);
    }

    /// <summary>
    /// Each surface drawn over its tiles into the G-buffer's targets, cleared first.
    /// </summary>
    internal void Resolve(RenderView view, in SurfaceTiles tiles, GpuTexture ids, in GBuffer targets)
    {
        TargetFormats formats = new(
            _gpu.GetFormat(targets.Albedo),
            _gpu.GetFormat(targets.Normal),
            _gpu.GetFormat(targets.Surface),
            _gpu.GetFormat(targets.Emissive),
            _gpu.GetFormat(targets.Velocity),
            GpuFormat.Invalid);
        PassConstants<MaterialConstants> constants = new(
            _passes.GetHeader(view),
            new MaterialConstants(tiles.Pieces, tiles.Tiles, ids));
        using GpuDebugGroup group = _gpu.Commands.DebugGroup("Resolve");
        using GpuRenderPass render = _gpu.Commands.RenderPass(
            [
                new GpuTextureLevel(targets.Albedo),
                new GpuTextureLevel(targets.Normal),
                new GpuTextureLevel(targets.Surface),
                new GpuTextureLevel(targets.Emissive),
                new GpuTextureLevel(targets.Velocity),
            ],
            GpuContents.Clear);
        render.Reads([ids, tiles.Pieces, tiles.Tiles]).IndexBuffer(GetIndices(), GpuIndexSize.Bits32);
        for (int bin = 0; bin < _materials.ResolveBins.Count; bin++)
        {
            SurfaceKey surface = _materials.ResolveBins[bin];
            DrawBin(render, MaterialPass.Resolve, surface, formats, constants, tiles.Draws, (uint)bin);
        }
    }

    /// <summary>
    /// The bytes a view's depth pyramid takes, every level from half the view down to a texel, and how many levels.
    /// </summary>
    private static (uint Bytes, uint Levels) MeasureHiZ(Size view)
    {
        uint texels = 0;
        uint levels = 0;
        Size size = view;
        do
        {
            size = new Size(Math.Max((size.Width + 1) / 2, 1), Math.Max((size.Height + 1) / 2, 1));
            texels += (uint)(size.Width * size.Height);
            levels++;
        }
        while (size.Width > 1 || size.Height > 1);

        return (texels * 4, levels);
    }

    /// <summary>
    /// Draws one bin with <paramref name="surface"/>'s pipeline for <paramref name="pass"/>, its arguments at row
    /// <paramref name="row"/>; none until its pipeline is made.
    /// </summary>
    private void DrawBin(
        GpuRenderPass render,
        MaterialPass pass,
        SurfaceKey surface,
        TargetFormats formats,
        in PassConstants<MaterialConstants> constants,
        GpuBuffer arguments,
        uint row)
    {
        GpuPipeline pipeline = _pipelines.GetMaterial(pass, surface, formats);

        if (pipeline == GpuPipeline.None) return;

        render.Pipeline(pipeline).Constants(constants).DrawIndexedFromBuffer(arguments, row * DrawBytes, 1);
    }

    /// <summary>
    /// The view's buffers, made again when its size or the world's capacity changed; the view counts as drawn this
    /// frame.
    /// </summary>
    private ViewBuffers GetBuffers(RenderView view)
    {
        _drawn.Add(view.Id);
        if (_views.TryGetValue(view.Id, out ViewBuffers? kept) && kept.Size == view.Size && kept.Capacity == _capacity)
            return kept;

        kept?.Release(_gpu);
        (uint hiZBytes, uint hiZLevels) = MeasureHiZ(view.Size);
        uint tiles = (uint)(((view.Size.Width + 7) / 8) * ((view.Size.Height + 7) / 8));
        ViewBuffers made = new(view.Size, _capacity, hiZLevels)
        {
            Counts = CreateBuffer(Counts * 4),
            InstanceArgs = CreateBuffer(DispatchBytes),
            ClusterArgs = CreateBuffer(2 * DispatchBytes),
            PieceArgs = CreateBuffer(2 * DispatchBytes),
            Budget = CreateBuffer(Buckets * 2 * 4),
            Jobs = CreateBuffer(_capacity.Jobs * 16),
            HiddenInstances = CreateBuffer(_capacity.Instances * 4),
            Clusters = CreateBuffer(_capacity.Clusters * 16),
            HiddenClusters = CreateBuffer(_capacity.Clusters * 8),
            BinCounts = CreateBuffer(RasterBins * 4),
            BinCursors = CreateBuffer(RasterBins * 4),
            BinArgs = CreateBuffer(2 * RasterBins * DrawBytes),
            Pieces = CreateBuffer(_capacity.Pieces * 12),
            HiZ = CreateBuffer(hiZBytes),
            TileCounts = CreateBuffer((ResolveRows + 1) * 4),
            TileList = CreateBuffer(Math.Max(_capacity.ResolveBins, 1) * tiles * 4),
            ResolveArgs = CreateBuffer(ResolveRows * DrawBytes),
        };
        _views[view.Id] = made;

        return made;
    }

    private GpuBuffer CreateBuffer(uint bytes) => _gpu.CreateZeroedBuffer(Usage, bytes);

    private GpuBuffer GetIndices()
    {
        if (_indices != GpuBuffer.None) return _indices;

        Span<uint> corners = stackalloc uint[PieceIndices];
        for (int i = 0; i < PieceIndices; i++)
        {
            corners[i] = (uint)i;
        }

        _indices = _gpu.CreateBuffer(GpuBufferUsage.Index, PieceIndices * sizeof(uint));
        _gpu.Upload(_indices, 0, MemoryMarshal.AsBytes(corners));

        return _indices;
    }

    /// <summary>
    /// The culling passes' constants, the view's culling buffers and the phase, as Culling.slang's CullConstants.
    /// </summary>
    [ShaderStruct("Shaders/Renderer/Culling.slang", "CullConstants")]
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Culling(
        GpuBuffer Counts,
        GpuBuffer InstanceArgs,
        GpuBuffer ClusterArgs,
        GpuBuffer PieceArgs,
        GpuBuffer Budget,
        GpuBuffer Jobs,
        GpuBuffer HiddenInstances,
        GpuBuffer Clusters,
        GpuBuffer HiddenClusters,
        GpuBuffer BinCounts,
        GpuBuffer BinCursors,
        GpuBuffer BinArgs,
        GpuBuffer Pieces,
        GpuBuffer HiZ,
        uint Phase);

    [ShaderStruct("Shaders/Renderer/DepthPyramid.slang", "DepthPyramidConstants")]
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct DepthPyramidConstants(
        GpuTexture Depth,
        GpuBuffer HiZ,
        GpuBuffer Counters,
        uint Levels);

    /// <summary>
    /// What every draw of the world's surfaces is handed, both its stages, as Shaders/Renderer/Materials.slang's
    /// <c>MaterialConstants</c>: the view's pieces, and for the resolve its tile list and the visibility buffer.
    /// </summary>
    [ShaderStruct("Shaders/Renderer/Materials.slang", "MaterialConstants")]
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct MaterialConstants(GpuBuffer Pieces, GpuBuffer Tiles, GpuTexture Visibility);

    [ShaderStruct("Shaders/Renderer/Materials.slang", "ClassifyConstants")]
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct ClassifyConstants(
        GpuTexture Visibility,
        GpuBuffer Pieces,
        GpuBuffer TileCounts,
        GpuBuffer TileList,
        GpuBuffer ResolveArgs);

    /// <summary>
    /// One view's culling and resolve buffers, for the size and capacity they were made for, and its depth pyramid's
    /// levels.
    /// </summary>
    private sealed class ViewBuffers(Size size, WorldCapacity capacity, uint hiZLevels)
    {
        internal Size Size { get; } = size;

        internal WorldCapacity Capacity { get; } = capacity;

        internal uint HiZLevels { get; } = hiZLevels;

        internal required GpuBuffer Counts { get; init; }

        internal required GpuBuffer InstanceArgs { get; init; }

        internal required GpuBuffer ClusterArgs { get; init; }

        internal required GpuBuffer PieceArgs { get; init; }

        internal required GpuBuffer Budget { get; init; }

        internal required GpuBuffer Jobs { get; init; }

        internal required GpuBuffer HiddenInstances { get; init; }

        internal required GpuBuffer Clusters { get; init; }

        internal required GpuBuffer HiddenClusters { get; init; }

        internal required GpuBuffer BinCounts { get; init; }

        internal required GpuBuffer BinCursors { get; init; }

        internal required GpuBuffer BinArgs { get; init; }

        internal required GpuBuffer Pieces { get; init; }

        internal required GpuBuffer HiZ { get; init; }

        internal required GpuBuffer TileCounts { get; init; }

        internal required GpuBuffer TileList { get; init; }

        internal required GpuBuffer ResolveArgs { get; init; }

        internal Culling ToCulling(uint phase) => new(
            Counts,
            InstanceArgs,
            ClusterArgs,
            PieceArgs,
            Budget,
            Jobs,
            HiddenInstances,
            Clusters,
            HiddenClusters,
            BinCounts,
            BinCursors,
            BinArgs,
            Pieces,
            HiZ,
            phase);

        internal void Release(IGpuDriver gpu)
        {
            foreach (GpuBuffer buffer in (ReadOnlySpan<GpuBuffer>)
                [
                    Counts, InstanceArgs, ClusterArgs, PieceArgs, Budget, Jobs, HiddenInstances, Clusters,
                    HiddenClusters, BinCounts, BinCursors, BinArgs, Pieces, HiZ, TileCounts, TileList, ResolveArgs,
                ])
            {
                gpu.Release(buffer);
            }
        }
    }
}
