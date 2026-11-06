using System.Numerics;
using Magic.Debugging;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Gpu;
using Magic.Rendering;
using Magic.Utils;

namespace Magic.GIRendering;

/// <summary>
/// The light table and the tiles of the screen each light reaches: the world's local lights uploaded each frame, and a
/// pass that sets each tile's bit for every light that touches what the tile shows.
/// </summary>
internal sealed class LightTiles(IGraphicsApi gpu, GpuPipelines pipelines) : IDisposable
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

    /// <summary>
    /// The frame's lights as the table holds them, written from the snapshots before they upload.
    /// </summary>
    private readonly GpuLight[] _lights = new GpuLight[MaxLights];

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
    /// The tiles made again for a picture of a size; the old ones destroyed.
    /// </summary>
    public Result Resize(int width, int height)
    {
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
    /// The world's lights into the table, point lights first, in a transfer pass the caller opened.
    /// </summary>
    public void Prepare(
        RenderGraph graph,
        EcsSnapshot<Transform, PointLight> points,
        EcsSnapshot<Transform, SpotLight> spots,
        EcsSnapshot<Transform, RectLight> rects
    )
    {
        int total = points.Count + spots.Count + rects.Count;
        Count = Math.Min(total, MaxLights);
        if (total > MaxLights && !_isFullLogged)
        {
            _isFullLogged = true;
            Log.Error($"Some lights are not drawn: the light table holds {MaxLights}.");
        }

        if (Count == 0)
            return;

        int row = 0;
        for (int i = 0; i < points.Count && row < Count; i++)
        {
            _lights[row++] = ToGpuLight(points.First[i], points.Second[i]);
        }

        for (int i = 0; i < spots.Count && row < Count; i++)
        {
            _lights[row++] = ToGpuLight(spots.First[i], spots.Second[i]);
        }

        for (int i = 0; i < rects.Count && row < Count; i++)
        {
            _lights[row++] = ToGpuLight(rects.First[i], rects.Second[i]);
        }

        graph.Upload(gpu, Table, _lights.AsSpan(0, Count));
    }

    /// <summary>
    /// Each tile's lights, from the view's depth.
    /// </summary>
    public void Record(in RenderView view, in GpuConstants header, GpuBuffer lightingConstants)
    {
        FrameTargets targets = view.Targets;
        _cullPass
            .Run(view.Graph, header)
            .Reads(view.Constants, lightingConstants, Table, targets.Depth)
            .Writes(Tiles)
            .Dispatch((uint)GetTiles(targets.Width), (uint)GetTiles(targets.Height));
    }

    private static int GetTiles(int pixels) => (pixels + TileSize - 1) / TileSize;

    private static GpuLight ToGpuLight(Transform at, PointLight light) =>
        new()
        {
            Position = at.Position,
            Range = light.Reach,
            Direction = Vector3.UnitZ,
            Radius = light.Radius,
            Radiance = new Vector3(light.Color.R, light.Color.G, light.Color.B) * light.Intensity,
            Type = GpuLight.Point,
        };

    private static GpuLight ToGpuLight(Transform at, SpotLight light) =>
        new()
        {
            Position = at.Position,
            Range = light.Reach,
            Direction = Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, at.Rotation)),
            Radius = light.Radius,
            Radiance = new Vector3(light.Color.R, light.Color.G, light.Color.B) * light.Intensity,
            Type = GpuLight.Spot,
            CosOuter = MathF.Cos(light.OuterAngle * MathF.PI / 180),
            CosInner = MathF.Cos(light.FadeAngle * MathF.PI / 180),
        };

    private static GpuLight ToGpuLight(Transform at, RectLight light) =>
        new()
        {
            Position = at.Position,
            Range = light.Reach,
            Direction = Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, at.Rotation)),
            Radius = 0.01f,
            Radiance = new Vector3(light.Color.R, light.Color.G, light.Color.B) * light.Intensity,
            Type = GpuLight.Rect,
            Width = light.Width * Math.Abs(at.Scale.X),
            Height = light.Height * Math.Abs(at.Scale.Y),
        };
}
