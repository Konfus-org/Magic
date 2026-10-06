using System.Drawing;

namespace DeferredRendererGem;

/// <summary>
/// The numbers a pass file sizes and dispatches by, as <c>$name</c>s: what the tables hold this frame (among them how
/// many lights cast shadows, the sun counted), and the size of the view or render target the pass runs over. Built
/// once per frame and narrowed per target and per view.
/// </summary>
internal readonly record struct FrameCounts(
    uint PageCount,
    uint ChunkCount,
    uint GroupCount,
    uint InstanceHighWater,
    uint VisibleHighWater,
    uint LightCount,
    uint GlowCount,
    uint MeshCount,
    uint BrickJobs,
    uint HiZFloats,
    uint ShadowCasters,
    uint TransparentChunks,
    uint RefractiveChunks,
    uint ViewWidth,
    uint ViewHeight,
    uint ViewTiles,
    uint TargetWidth,
    uint TargetHeight)
{
    /// <summary>
    /// Every name a file may write, with the <c>$</c>.
    /// </summary>
    public static readonly string[] Names =
    [
        "$pageCount", "$chunkCount", "$groupCount", "$instanceHighWater", "$visibleHighWater", "$lightCount", "$glowCount",
        "$meshCount", "$brickJobs", "$hiZFloats", "$shadowCasters", "$transparentChunks", "$refractiveChunks", "$viewWidth", "$viewHeight", "$viewTiles", "$targetWidth", "$targetHeight",
    ];

    /// <summary>
    /// The frame's counts, from the tables; the view and target sizes are 0 until narrowed.
    /// </summary>
    public static FrameCounts Of(RenderContext ctx, uint lightCount, uint brickJobs, uint shadowCasters)
    {
        return new FrameCounts(
            (uint)ctx.Instances.PageCount,
            (uint)ctx.Buckets.ChunkCount,
            (uint)ctx.Buckets.ChunkCount * Buckets.GroupsPerChunk,
            ctx.Instances.HighWater,
            ctx.Buckets.VisibleHighWater,
            lightCount,
            ctx.GlowCount,
            (uint)ctx.Meshes.MeshCount,
            brickJobs,
            0, shadowCasters, CountChunks(ctx, SurfaceVariant.Transparent), CountChunks(ctx, SurfaceVariant.Refractive), 0, 0, 0, 0, 0);
    }

    /// <summary>
    /// The bucket chunks of classes with <paramref name="variant"/> that hold anything: none transparent, and the
    /// transparency stage is skipped; none refractive, and its glass layers are.
    /// </summary>
    private static uint CountChunks(RenderContext ctx, SurfaceVariant variant)
    {
        uint count = 0;
        foreach ((PipelineClass cls, List<int> chunks) in ctx.Buckets.ByClass)
        {
            if (!cls.Variant.HasFlag(variant))
                continue;

            foreach (int chunk in chunks)
            {
                if (!ctx.Buckets.IsEmpty(chunk))
                    count++;
            }
        }

        return count;
    }

    public static bool IsName(string name)
    {
        return Array.IndexOf(Names, name) >= 0;
    }

    public FrameCounts WithTarget(uint width, uint height)
    {
        return this with { TargetWidth = width, TargetHeight = height };
    }

    /// <summary>
    /// Pixels a side of the screen tiles <c>$viewTiles</c> counts: the lighting's (twin: <c>LightTileSize</c>).
    /// </summary>
    public const int TilePixels = 32;

    public FrameCounts WithView(Rectangle rect, uint hiZFloats)
    {
        uint width = (uint)Math.Max(0, rect.Width), height = (uint)Math.Max(0, rect.Height);
        uint tiles = ((width + TilePixels - 1) / TilePixels) * ((height + TilePixels - 1) / TilePixels);
        return this with { ViewWidth = width, ViewHeight = height, ViewTiles = tiles, HiZFloats = hiZFloats };
    }

    /// <summary>
    /// The value of a <c>$name</c>; false for a name that is not one.
    /// </summary>
    public bool TryResolve(string name, out uint value)
    {
        value = name switch
        {
            "$pageCount" => PageCount,
            "$chunkCount" => ChunkCount,
            "$groupCount" => GroupCount,
            "$instanceHighWater" => InstanceHighWater,
            "$visibleHighWater" => VisibleHighWater,
            "$lightCount" => LightCount,
            "$glowCount" => GlowCount,
            "$meshCount" => MeshCount,
            "$brickJobs" => BrickJobs,
            "$hiZFloats" => HiZFloats,
            "$shadowCasters" => ShadowCasters,
            "$transparentChunks" => TransparentChunks,
            "$refractiveChunks" => RefractiveChunks,
            "$viewWidth" => ViewWidth,
            "$viewHeight" => ViewHeight,
            "$viewTiles" => ViewTiles,
            "$targetWidth" => TargetWidth,
            "$targetHeight" => TargetHeight,
            _ => uint.MaxValue,
        };

        return value != uint.MaxValue;
    }
}
