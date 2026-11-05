using System.Runtime.InteropServices;
using Magic.Gpu;
using Magic.Rendering;
using Magic.Utils;

namespace Magic.GIRendering;

/// <summary>
/// The frame's lights: their table, and the froxel lists of the view they are binned into, so a pixel loops over its own
/// froxel's lights alone.
/// </summary>
internal sealed class LightBins(IGraphicsApi gpu, RenderGraph graph, GpuPipelines pipelines) : IDisposable
{
    private const int MaxLights = 4096;
    private const int GridX = 32;
    private const int GridY = 18;
    private const int GridZ = 48;
    private const int LightsPerFroxel = 128;
    private const int FroxelCount = GridX * GridY * GridZ;
    private const int BinThreads = 64;

    private readonly GpuPass _binPass = pipelines.Compute("Bin lights", "BinLights", "BinLights");
    private readonly GpuLight[] _lights = new GpuLight[MaxLights];

    public GpuBuffer Table { get; private set; }

    public GpuBuffer FroxelLists { get; private set; }

    /// <summary>
    /// How many lights the frame has, up to what the table holds.
    /// </summary>
    public int Count { get; private set; }

    public void Dispose()
    {
        foreach (GpuBuffer buffer in (ReadOnlySpan<GpuBuffer>)[Table, FroxelLists])
        {
            if (!buffer.IsNone)
                gpu.Destroy(buffer);
        }
    }

    public Result Create()
    {
        Result<GpuBuffer> table = gpu.CreateBuffer(
            new BufferDescription
            {
                Bytes = (long)MaxLights * GpuLight.Bytes,
                Usage = BufferUsages.Storage,
                Name = "Light table",
            }
        );
        if (!table.Ok)
            return Result.Failure($"the Light table buffer was not made: {table.Error}");

        Table = table.Value;
        Result<GpuBuffer> lists = gpu.CreateBuffer(
            new BufferDescription
            {
                Bytes = (long)FroxelCount * (LightsPerFroxel + 1) * 4,
                Usage = BufferUsages.Storage,
                Name = "Froxel light lists",
            }
        );
        if (!lists.Ok)
            return Result.Failure($"the Froxel light lists buffer was not made: {lists.Error}");

        FroxelLists = lists.Value;

        return Result.Success();
    }

    /// <summary>
    /// The frame's lights uploaded and the froxel lists emptied, in a transfer pass the caller opened.
    /// </summary>
    public void Prepare(RenderWorld world)
    {
        Count = Math.Min(world.Lights.Count, MaxLights);
        world.Lights.CopyTo(0, _lights, 0, Count);
        if (Count > 0)
        {
            int bytes = Count * GpuLight.Bytes;
            GpuUpload lights = gpu.BeginUpload(bytes);
            MemoryMarshal.AsBytes(_lights.AsSpan(0, Count)).CopyTo(lights.Bytes);
            graph.Use(Table, GpuAccess.CopyDestination);
            graph.Recorder.CopyBuffer(lights.Buffer, lights.Offset, Table, 0, (ulong)bytes);
        }

        graph.Use(FroxelLists, GpuAccess.CopyDestination);
        graph.Recorder.FillBuffer(FroxelLists, 0);
    }

    public void Record(in GpuConstants header, GpuBuffer frameConstants, GpuBuffer lightingConstants) =>
        _binPass
            .Run(header)
            .Reads(frameConstants, lightingConstants, Table)
            .Writes(FroxelLists)
            .Dispatch((uint)((Count + BinThreads - 1) / BinThreads));
}
