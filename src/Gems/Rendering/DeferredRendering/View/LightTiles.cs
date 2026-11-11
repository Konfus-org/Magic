using System.Collections.Immutable;
using System.Numerics;
using Magic.Assets;
using Magic.Debugging;
using Magic.DeferredRendering.World;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Graphics;
using Magic.Utils;

namespace Magic.DeferredRendering.View;

/// <summary>
/// A frame's light table, holding <paramref name="Count"/> lights, and each tile's words of bits, a bit per light.
/// </summary>
internal readonly record struct LightBuffers(GpuBuffer Table, GpuBuffer Tiles, int Count);

/// <summary>
/// The light table and the tiles of the screen each light reaches: the world's local lights uploaded each frame, and a
/// pass that sets each tile's bit for every light that touches what the tile shows.
/// </summary>
internal sealed class LightTiles(AssetManager assets)
{
    /// <summary>
    /// The twin of <c>Scene/Lights.slang</c>'s.
    /// </summary>
    public const int TileSize = 32;

    /// <summary>
    /// The twin of <c>Scene/Lights.slang</c>'s.
    /// </summary>
    public const int MaxLights = 1024;

    private const int Buckets = MaxLights / 32;

    /// <summary>
    /// The frame's lights as the table holds them, written from the snapshots before they upload.
    /// </summary>
    private readonly GpuLight[] _lights = new GpuLight[MaxLights];

    private readonly ShaderEntry _cullShader = PassShaders.GetEntry(assets, "CullLights", "CullLights");

    /// <summary>
    /// How many lights the last frame had, so the error shows each time the table fills, not each frame while it is.
    /// </summary>
    private int _lastTotal;

    public ImmutableArray<ShaderEntry> Shaders => [_cullShader];

    /// <summary>
    /// The table and the view's tiles asked for, and the world's lights into the table, point lights first, in a
    /// transfer pass the caller opened.
    /// </summary>
    public Result<LightBuffers> Prepare(
        RenderGraph graph,
        int width,
        int height,
        EcsSnapshot<Transform, PointLight> points,
        EcsSnapshot<Transform, SpotLight> spots,
        EcsSnapshot<Transform, RectLight> rects
    )
    {
        Result<GpuBuffer> table = graph.Buffer(GpuDescriptions.GetBuffer("Light table", MaxLights * GpuLight.Bytes));
        if (!table.Ok)
            return Result.Failure(table.Error);

        Result<GpuBuffer> tiles = graph.Buffer(
            GpuDescriptions.GetBuffer("Light tiles", (long)GetTiles(width) * GetTiles(height) * Buckets * sizeof(uint))
        );
        if (!tiles.Ok)
            return Result.Failure(tiles.Error);

        int total = points.Count + spots.Count + rects.Count;
        LightBuffers lights = new(table.Value, tiles.Value, Math.Min(total, MaxLights));
        if (total > MaxLights && _lastTotal <= MaxLights)
            Log.Error($"Some lights are not drawn: the light table holds {MaxLights}.");

        _lastTotal = total;

        if (lights.Count == 0)
            return Result.Success(lights);

        int row = 0;
        for (int i = 0; i < points.Count && row < lights.Count; i++)
        {
            _lights[row++] = ToGpuLight(points.First[i], points.Second[i]);
        }

        for (int i = 0; i < spots.Count && row < lights.Count; i++)
        {
            _lights[row++] = ToGpuLight(spots.First[i], spots.Second[i]);
        }

        for (int i = 0; i < rects.Count && row < lights.Count; i++)
        {
            _lights[row++] = ToGpuLight(rects.First[i], rects.Second[i]);
        }

        graph.Upload(lights.Table, _lights.AsSpan(0, lights.Count));

        return Result.Success(lights);
    }

    /// <summary>
    /// Each tile's lights, from the view's depth.
    /// </summary>
    public void Record(RenderGraph graph, in GpuView view, in GpuConstants header)
    {
        graph
            .Compute("Cull lights", _cullShader, header, RenderStage.Light)
            .Reads(view.Frame, view.LightTable, view.Depth)
            .Writes(view.LightTiles)
            .Dispatch((uint)GetTiles(view.Width), (uint)GetTiles(view.Height));
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
