using System.Numerics;
using System.Runtime.InteropServices;
using Magic.Gpu;
using Magic.Rendering;
using Magic.Utils;

namespace GIRendering;

/// <summary>
/// The sun's virtual shadow map: its page table, the pool of physical pages, the lists the frame's passes fill, and
/// the passes themselves. Each frame the receivers mark the pages they need from the depth buffer, the instances
/// that changed invalidate the pages under them, unmarked pages go back to the pool, marked pages without one take
/// one, and only the invalid pages are cleared and drawn, so a still scene draws no shadow geometry at all.
/// </summary>
internal sealed class SunShadows(IGraphicsApi gpu, GpuPipelines pipelines) : IDisposable
{
    public const int Levels = 20;

    public const int TableWidth = 128;

    public const int PageTexels = 128;

    public const int PoolPagesPerRow = 32;

    public const int PoolPages = PoolPagesPerRow * PoolPagesPerRow;

    public const int PoolTexels = PoolPagesPerRow * PageTexels;

    public const int MaxInvalidPages = 2048;

    public const int MaxDraws = 1 << 18;

    public const float BaseTexel = 8f / 16384;

    private const int Cells = Levels * TableWidth * TableWidth;

    private const int CountsBytes = 64;

    private const int ClearArgumentsBytes = 12;

    private const int DrawCountOffset = 4;

    private const int DrawArgumentsBytes = 16;

    private const int CullThreads = 64;

    private const int CullGroupsPerRow = 65535;

    private readonly GpuPass _markPagesPass = pipelines.Compute(
        "Shadow: mark pages",
        "ShadowPages",
        "ShadowMarkPages",
        isTiled: true
    );

    private readonly GpuPass _invalidatePagesPass = pipelines.Compute(
        "Shadow: invalidate pages",
        "ShadowPages",
        "ShadowInvalidatePages"
    );

    private readonly GpuPass _freePagesPass = pipelines.Compute("Shadow: free pages", "ShadowPages", "ShadowFreePages");

    private readonly GpuPass _allocatePagesPass = pipelines.Compute(
        "Shadow: allocate pages",
        "ShadowPages",
        "ShadowAllocatePages"
    );

    private readonly GpuPass _clearPagesPass = pipelines.Compute(
        "Shadow: clear pages",
        "ShadowPages",
        "ShadowClearPages",
        isTiled: true
    );

    private readonly GpuPass _cullPass = pipelines.Compute("Shadow: cull clusters", "ShadowCull", "ShadowCull");

    private readonly GpuPass _drawPagesPass = pipelines.Draw(
        "Shadow: draw pages",
        "DrawShadow",
        "DrawShadowVertex",
        "DrawShadowPixel",
        state => state with { Cull = CullMode.None }
    );

    private readonly GpuPass _finishPagesPass = pipelines.Compute(
        "Shadow: finish pages",
        "ShadowPages",
        "ShadowFinishPages"
    );

    private GpuBuffer _pageTable;

    private GpuBuffer _pageRequests;

    private GpuBuffer _freeList;

    private GpuBuffer _invalidPages;

    private GpuBuffer _draws;

    private GpuBuffer _drawArguments;

    private GpuBuffer _counts;

    private GpuBuffer _clearArguments;

    private GpuBuffer _dirtyInstances;

    private GpuTexture _pool;

    private bool _isTableReady;

    private int _dirtyCount;

    public GpuBuffer Constants { get; private set; }

    public void Dispose()
    {
        foreach (
            GpuBuffer buffer in (ReadOnlySpan<GpuBuffer>)
                [
                    Constants,
                    _pageTable,
                    _pageRequests,
                    _freeList,
                    _invalidPages,
                    _draws,
                    _drawArguments,
                    _counts,
                    _clearArguments,
                    _dirtyInstances,
                ]
        )
        {
            if (!buffer.IsNone)
                gpu.Destroy(buffer);
        }

        if (!_pool.IsNone)
            gpu.Destroy(_pool);
    }

    public Result Create()
    {
        (string Name, long Bytes, BufferUsages Usage)[] descriptions =
        [
            ("Shadow constants", ShadowConstants.Bytes, BufferUsages.Storage),
            ("Shadow page table", (long)Cells * 8, BufferUsages.Storage),
            ("Shadow page requests", (long)Cells * 4, BufferUsages.Storage),
            ("Shadow free pages", (long)(PoolPages + 1) * 4, BufferUsages.Storage),
            ("Shadow invalid pages", (long)MaxInvalidPages * 8, BufferUsages.Storage),
            ("Shadow draws", (long)MaxDraws * 16, BufferUsages.Storage),
            (
                "Shadow draw arguments",
                (long)MaxDraws * DrawArgumentsBytes,
                BufferUsages.Storage | BufferUsages.Indirect
            ),
            ("Shadow counts", CountsBytes, BufferUsages.Storage | BufferUsages.Indirect),
            ("Shadow clear arguments", ClearArgumentsBytes, BufferUsages.Storage | BufferUsages.Indirect),
            ("Shadow dirty instances", (long)InstanceTable.Capacity * DirtyInstance.Bytes, BufferUsages.Storage),
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

        (
            Constants,
            _pageTable,
            _pageRequests,
            _freeList,
            _invalidPages,
            _draws,
            _drawArguments,
            _counts,
            _clearArguments,
            _dirtyInstances
        ) = (made[0], made[1], made[2], made[3], made[4], made[5], made[6], made[7], made[8], made[9]);
        Result<GpuTexture> pool = gpu.CreateTexture(
            new TextureDescription
            {
                Width = PoolTexels,
                Height = PoolTexels,
                Format = GpuFormat.R32Uint,
                Usage = TextureUsages.Storage | TextureUsages.Sampled,
                Name = "Shadow page pool",
            }
        );
        if (!pool.Ok)
            return Result.Failure($"the shadow page pool was not made: {pool.Error}");

        _pool = pool.Value;

        return Result.Success();
    }

    /// <summary>
    /// The frame's constants and lists uploaded and its counts reset, in a transfer pass the caller opened: the
    /// light's frame from the sun, each level's window around the camera, and the instances that changed.
    /// </summary>
    public void Prepare(
        RenderGraph graph,
        Vector3 cameraPosition,
        Vector3 sunDirection,
        float footprintPerMetre,
        ReadOnlySpan<DirtyInstance> dirty
    )
    {
        if (!_isTableReady)
        {
            _isTableReady = true;
            graph.Use(_pageTable, GpuAccess.CopyDestination);
            graph.Recorder.FillBuffer(_pageTable, 0);
            GpuUpload free = gpu.BeginUpload((PoolPages + 1) * 4);
            Span<uint> words = MemoryMarshal.Cast<byte, uint>(free.Bytes);
            words[0] = PoolPages;
            for (int page = 0; page < PoolPages; page++)
            {
                words[1 + page] = (uint)page;
            }

            graph.Use(_freeList, GpuAccess.CopyDestination);
            graph.Recorder.CopyBuffer(free.Buffer, free.Offset, _freeList, 0, (ulong)(PoolPages + 1) * 4);
        }

        Vector3 z = -sunDirection;
        Vector3 up = Math.Abs(z.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
        Vector3 x = Vector3.Normalize(Vector3.Cross(up, z));
        Vector3 y = Vector3.Cross(z, x);
        Vector3 cameraLight = new(
            Vector3.Dot(cameraPosition, x),
            Vector3.Dot(cameraPosition, y),
            Vector3.Dot(cameraPosition, z)
        );
        _dirtyCount = Math.Min(dirty.Length, InstanceTable.Capacity);
        ShadowConstants constants = new()
        {
            LightX = new Vector4(x, 0),
            LightY = new Vector4(y, 0),
            LightZ = new Vector4(z, 0),
            CameraLight = new Vector4(cameraLight, 0),
            Levels = Levels,
            TableWidth = TableWidth,
            PageTexels = PageTexels,
            PoolPagesPerRow = PoolPagesPerRow,
            BaseTexel = BaseTexel,
            FootprintPerMetre = footprintPerMetre,
            MaxDraws = MaxDraws,
            MaxInvalidPages = MaxInvalidPages,
            PageTable = _pageTable.Id,
            PageRequests = _pageRequests.Id,
            FreeList = _freeList.Id,
            InvalidPages = _invalidPages.Id,
            Draws = _draws.Id,
            DrawArguments = _drawArguments.Id,
            Counts = _counts.Id,
            Pool = _pool.Id,
            DirtyInstances = _dirtyInstances.Id,
            DirtyCount = (uint)_dirtyCount,
            ClearArguments = _clearArguments.Id,
        };
        for (int level = 0; level < Levels; level++)
        {
            float pageWorld = BaseTexel * PageTexels * MathF.Pow(2, level);
            constants.Windows[level] = new ShadowWindow(
                (int)MathF.Floor(cameraLight.X / pageWorld) - (TableWidth / 2),
                (int)MathF.Floor(cameraLight.Y / pageWorld) - (TableWidth / 2),
                0,
                0
            );
        }

        GpuUpload upload = gpu.BeginUpload(ShadowConstants.Bytes);
        MemoryMarshal.Write(upload.Bytes, in constants);
        graph.Use(Constants, GpuAccess.CopyDestination);
        graph.Recorder.CopyBuffer(upload.Buffer, upload.Offset, Constants, 0, ShadowConstants.Bytes);

        if (_dirtyCount > 0)
        {
            int bytes = _dirtyCount * DirtyInstance.Bytes;
            GpuUpload changes = gpu.BeginUpload(bytes);
            MemoryMarshal.AsBytes(dirty[.._dirtyCount]).CopyTo(changes.Bytes);
            graph.Use(_dirtyInstances, GpuAccess.CopyDestination);
            graph.Recorder.CopyBuffer(changes.Buffer, changes.Offset, _dirtyInstances, 0, (ulong)bytes);
        }

        graph.Use(_pageRequests, GpuAccess.CopyDestination);
        graph.Recorder.FillBuffer(_pageRequests, uint.MaxValue);
        graph.Use(_counts, GpuAccess.CopyDestination);
        graph.Recorder.FillBuffer(_counts, 0);
        GpuUpload clear = gpu.BeginUpload(ClearArgumentsBytes);
        Span<uint> groups = MemoryMarshal.Cast<byte, uint>(clear.Bytes);
        groups[0] = PageTexels / 8;
        groups[1] = PageTexels / 8;
        groups[2] = 0;
        graph.Use(_clearArguments, GpuAccess.CopyDestination);
        graph.Recorder.CopyBuffer(clear.Buffer, clear.Offset, _clearArguments, 0, ClearArgumentsBytes);
    }

    /// <summary>
    /// The frame's shadow passes, after the view's depth is drawn and before it is shaded.
    /// </summary>
    public void Record(
        RenderGraph graph,
        GpuBuffer frameConstants,
        GpuBuffer instances,
        int instanceCount,
        GeometryPools pools,
        FrameTargets targets
    )
    {
        GpuConstants header = new(frameConstants.Id, Constants.Id);

        _markPagesPass
            .Run(graph, header)
            .Reads(frameConstants, Constants, targets.Depth)
            .Writes(_pageRequests)
            .Dispatch(Groups(targets.Width, pipelines.TileSize), Groups(targets.Height, pipelines.TileSize));

        if (_dirtyCount > 0)
        {
            _invalidatePagesPass
                .Run(graph, header)
                .Reads(Constants, _dirtyInstances)
                .Writes(_pageTable)
                .Dispatch(Groups(_dirtyCount, CullThreads));
        }

        _freePagesPass
            .Run(graph, header)
            .Reads(Constants, _pageRequests)
            .Writes(_pageTable, _freeList)
            .Dispatch(Groups(Cells, CullThreads));

        _allocatePagesPass
            .Run(graph, header)
            .Reads(Constants, _pageRequests)
            .Writes(_pageTable, _freeList, _invalidPages, _counts, _clearArguments)
            .Dispatch(Groups(Cells, CullThreads));

        _clearPagesPass
            .Run(graph, header)
            .Reads(Constants, _counts, _invalidPages)
            .Writes(_pool)
            .Indirect(_clearArguments)
            .DispatchIndirect(_clearArguments, 0);

        GpuPassRun cullRun = _cullPass
            .Run(graph, header)
            .Reads(frameConstants, Constants, instances, pools.Clusters, _pageTable)
            .Writes(_counts, _draws, _drawArguments);
        int rows = (instanceCount + CullGroupsPerRow - 1) / CullGroupsPerRow;
        cullRun.Dispatch((uint)Math.Min(instanceCount, CullGroupsPerRow), (uint)Math.Max(rows, 1));

        _drawPagesPass
            .Run(graph, header, new GpuRenderTargets { Width = PoolTexels, Height = PoolTexels })
            .Reads(
                frameConstants,
                Constants,
                instances,
                pools.Clusters,
                pools.Vertices,
                pools.Indices,
                _pageTable,
                _draws
            )
            .Writes(_pool)
            .Indirect(_drawArguments, _counts)
            .DrawIndirectCount(_drawArguments, 0, _counts, DrawCountOffset, MaxDraws, DrawArgumentsBytes);

        _finishPagesPass
            .Run(graph, header)
            .Reads(Constants, _counts, _invalidPages)
            .Writes(_pageTable)
            .Indirect(_clearArguments)
            .DispatchIndirect(_clearArguments, 0);
    }

    /// <summary>
    /// Says the shading pass reads the pages: what it declares before sampling them.
    /// </summary>
    public void UseForSampling(RenderGraph graph)
    {
        graph.Use(Constants, GpuAccess.ShaderRead);
        graph.Use(_pageTable, GpuAccess.ShaderRead);
        graph.Use(_pool, GpuAccess.ShaderRead);
    }

    private static uint Groups(int count, uint size) => (uint)((count + size - 1) / size);
}
