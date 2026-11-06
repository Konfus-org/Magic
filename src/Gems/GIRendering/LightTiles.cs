using System.Runtime.InteropServices;
using Magic.Debugging;
using Magic.Gpu;
using Magic.Rendering;
using Magic.Utils;

namespace Magic.GIRendering;

/// <summary>
/// The light table and the tiles of the screen each light reaches: the world's local lights uploaded each frame, and a
/// pass that sets each tile's bit for every light that touches what the tile shows.
/// </summary>
internal sealed class LightTiles(IGraphicsApi gpu, RenderGraph graph, GpuPipelines pipelines) : IDisposable
{
    /// <summary>
    /// The twin of <c>Scene/Lights.slang</c>'s.
    /// </summary>
    public const int MaxLights = 1024;

    /// <summary>
    /// The twin of <c>Scene/Lights.slang</c>'s.
    /// </summary>
    public const int TileSize = 32;

    private const int Buckets = MaxLights / 32;

    private readonly GpuPass _cullPass = pipelines.Compute("Cull lights", "CullLights", "CullLights");

    private bool _isFullLogged;

    public GpuBuffer Table { get; private set; }

    /// <summary>
    /// Each tile's words of bits, a bit per light.
    /// </summary>
    public GpuBuffer Tiles { get; private set; }

    /// <summary>
    /// How many lights the table holds this frame.
    /// </summary>
    public int Count { get; private set; }

    public void Dispose()
    {
        gpu.Destroy(Table);
        gpu.Destroy(Tiles);
    }

    public Result Create()
    {
        Result<GpuBuffer> table = gpu.CreateBuffer("Light table", (long)MaxLights * GpuLight.Bytes);
        if (!table.Ok)
            return Result.Failure(table.Error);

        Table = table.Value;

        return Result.Success();
    }

    /// <summary>
    /// The tiles made again for a picture of a size; the old ones destroyed and forgotten by the graph.
    /// </summary>
    public Result Resize(int width, int height)
    {
        graph.Forget(Tiles);
        gpu.Destroy(Tiles);
        Result<GpuBuffer> tiles = gpu.CreateBuffer(
            "Light tiles",
            (long)GetTiles(width) * GetTiles(height) * Buckets * sizeof(uint)
        );
        Tiles = tiles.Value;
        if (!tiles.Ok)
            return Result.Failure(tiles.Error);

        return Result.Success();
    }

    /// <summary>
    /// The world's lights into the table, in a transfer pass the caller opened.
    /// </summary>
    public void Prepare(RenderWorld world)
    {
        Count = Math.Min(world.Lights.Count, MaxLights);
        if (world.Lights.Count > MaxLights && !_isFullLogged)
        {
            _isFullLogged = true;
            Log.Error($"Some lights are not drawn: the light table holds {MaxLights}.");
        }

        if (Count == 0)
            return;

        graph.Upload(gpu, Table, CollectionsMarshal.AsSpan(world.Lights)[..Count]);
    }

    /// <summary>
    /// Each tile's lights, from the view's depth.
    /// </summary>
    public void Record(in RenderView view, in GpuConstants header, GpuBuffer lightingConstants)
    {
        FrameTargets targets = view.Targets;
        _cullPass
            .Run(header)
            .Reads(view.Constants, lightingConstants, Table, targets.Depth)
            .Writes(Tiles)
            .Dispatch((uint)GetTiles(targets.Width), (uint)GetTiles(targets.Height));
    }

    private static int GetTiles(int pixels) => (pixels + TileSize - 1) / TileSize;
}
