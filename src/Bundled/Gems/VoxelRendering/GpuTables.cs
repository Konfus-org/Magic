using Magic.Contexts.Gpu;
using Magic.Interfaces;

namespace Magic.VoxelRendering;

/// <summary>
/// Every table of the world the GPU reads, in one place: the instances, the models and their levels, clusters and
/// cards, the materials and how each is drawn. Each part writes the rows that are its own; once a frame, before
/// anything records, every row set since is sent (<see cref="Upload"/>). A view's constants name each by its buffer,
/// which changes as a table grows. Render thread.
/// </summary>
internal sealed class GpuTables : IDisposable
{
    private const GpuBufferUsage Usage = GpuBufferUsage.GraphicsRead | GpuBufferUsage.ComputeRead;

    private readonly IGpuTable[] _all;

    internal GpuTables(IGpuDriver gpu)
    {
        Instances = new GpuTable<GpuInstance>(gpu, Usage, 1024);
        Models = new GpuTable<GpuModel>(gpu, Usage, 256);
        Levels = new GpuTable<GpuLevel>(gpu, Usage, 1024);
        Clusters = new GpuTable<GpuCluster>(gpu, Usage, 4096);
        Cards = new GpuTable<GpuCard>(gpu, Usage, 256);
        Materials = new GpuTable<GpuMaterial>(gpu, Usage, 64);
        MaterialBins = new GpuTable<uint>(gpu, Usage, 64);
        _all = [Instances, Models, Levels, Clusters, Cards, Materials, MaterialBins];
    }

    /// <summary>
    /// An instance a row, at its entity's slot (<see cref="VoxelRendering.Instances"/>).
    /// </summary>
    internal GpuTable<GpuInstance> Instances { get; }

    internal GpuTable<GpuModel> Models { get; }

    internal GpuTable<GpuLevel> Levels { get; }

    internal GpuTable<GpuCluster> Clusters { get; }

    internal GpuTable<GpuCard> Cards { get; }

    /// <summary>
    /// A material's params, packed by its surface (<see cref="GpuMaterials"/>).
    /// </summary>
    internal GpuTable<GpuMaterial> Materials { get; }

    /// <summary>
    /// How each material is drawn, a word a row: its raster bin, its resolve bin and its flags
    /// (<see cref="GpuMaterials.WriteBins"/>).
    /// </summary>
    internal GpuTable<uint> MaterialBins { get; }

    public void Dispose()
    {
        foreach (IGpuTable table in _all)
        {
            table.Release();
        }
    }

    /// <summary>
    /// Sends every row set since the last upload.
    /// </summary>
    internal void Upload()
    {
        foreach (IGpuTable table in _all)
        {
            table.Upload();
        }
    }
}
