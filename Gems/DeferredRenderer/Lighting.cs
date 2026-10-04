using Magic.Contexts.Rendering;
using Magic.Extensions;
using Magic.Interfaces;
using System.Buffers;

namespace DeferredRendererGem;

/// <summary>
/// The lighting, as commands: after a render target's views are drawn into its <see cref="GBuffer"/>, two compute
/// passes per view light it into <c>Hdr</c>. The first bins the point and spot lights into the view's screen tiles
/// (<see cref="TileSize"/> pixels a side) by the depth drawn in each; the second cuts every tile along its depth into
/// <see cref="Slices"/> clusters, each with the tile's lights that reach that stretch of depth; the third shades every
/// pixel of the view from the gbuffer: what the surface emits, the sun and ambient of the frame constants, and its
/// cluster's lights. All of it is on
/// the GPU: the lights are uploaded as they are, every one of them, and nothing here decides which light reaches what.
/// Lighting is the engine's own step, always there; it is not a pass and no <c>PostProcessing</c> lists it.
/// </summary>
internal static class Lighting
{
    /// <summary>
    /// Pixels a side of a screen tile the lights are binned into. Smaller tiles fit their lights tighter, but there
    /// are more tiles to bin every light into, and four times the clusters for half the size. Twin:
    /// <c>LightTileSize</c> in Structs.hlsli.
    /// </summary>
    public const int TileSize = 32;

    /// <summary>
    /// Uints in a tile's row: its light count and two depths, then the light indices. A tile only has to hold every
    /// light along its line of sight roughly, for its clusters to pick from, so it is long; one more lights reach than
    /// fit shows as a failure. Twin: <c>LightTileWords</c>.
    /// </summary>
    public const int TileWords = 1024;

    /// <summary>
    /// The clusters a tile is cut into along the depth drawn in it. More slices mean fewer lights in each, so a cluster overflows
    /// later, for more memory and more clusters to fill. Twin: <c>LightSlices</c>.
    /// </summary>
    public const int Slices = 16;

    /// <summary>
    /// Uints in a cluster's row: its light count, then that many light indices, so one less than this many lights
    /// reach a pixel; a cluster more reach glows the failure magenta, lit by the first ones only. Twin:
    /// <c>LightClusterWords</c>.
    /// </summary>
    public const int ClusterWords = 64;

    private const int CullGroup = 8;  // LightCull.comp.hlsl: tiles a side per group

    private const int ClusterGroup = 4; // LightCluster.comp.hlsl: clusters a side per group, in depth too

    private const int ShadeGroup = 8; // Lighting.comp.hlsl: pixels a side per group

    /// <summary>
    /// Compiles the lighting compute shaders.
    /// </summary>
    public static LightingPipelines Create(RenderContext ctx)
    {
        IRendering gpu = ctx.Gpu;
        return new LightingPipelines(
            gpu.CreateComputePipeline(Shaders.CompileBuiltIn(ctx, "Lighting/LightCull.comp.hlsl", GpuStage.Compute)),
            gpu.CreateComputePipeline(Shaders.CompileBuiltIn(ctx, "Lighting/LightCluster.comp.hlsl", GpuStage.Compute)),
            gpu.CreateComputePipeline(Shaders.CompileBuiltIn(ctx, "Lighting/Lighting.comp.hlsl", GpuStage.Compute)));
    }

    /// <summary>
    /// The frame's point and spot lights into the lights buffer, in the order they come and all of them: the count
    /// the frame constants carry (<see cref="LightingConstants.From"/>) is how many rows this wrote.
    /// </summary>
    public static void Upload(RenderContext ctx, ReadOnlySpan<LightInstance> lights)
    {
        GpuLight[] rows = ArrayPool<GpuLight>.Shared.Rent(lights.Length);
        ShadowState shadows = ctx.Shadows;
        int count = 0;
        for (int i = 0; i < lights.Length; i++)
        {
            if (lights[i].Kind != LightKind.Directional)
                rows[count++] = GpuLight.From(lights[i], shadows.LightRecords[i], shadows.LightFaces[i]);
        }

        if (count > 0)
        {
            ctx.Lights.Ensure(ctx.Gpu, (uint)(count * GpuLight.Size));
            ctx.Gpu.Upload<GpuLight>(ctx.Lights.Handle, 0, rows.AsSpan(0, count));
        }

        ArrayPool<GpuLight>.Shared.Return(rows);
    }

    /// <summary>
    /// Before a frame is recorded: the view's tile and cluster rows fit its size.
    /// </summary>
    public static void Prepare(RenderContext ctx, ViewBuffers view, int viewWidth, int viewHeight)
    {
        (uint across, uint down) = Tiles(viewWidth, viewHeight);
        view.LightTiles.Ensure(ctx.Gpu, across * down * TileWords * sizeof(uint));
        view.LightClusters.Ensure(ctx.Gpu, across * down * Slices * ClusterWords * sizeof(uint));
    }

    /// <summary>
    /// Lights one view of the render target: its gbuffer pixels into <c>Hdr</c>. Outside any pass, after every draw
    /// into the gbuffer. Returns the dispatches recorded.
    /// </summary>
    public static int Record(RenderContext ctx, RenderCommands commands, FrameTargets targets, in ViewPlan view)
    {
        GBuffer gbuffer = targets.GBuffer;
        GpuSampler nearest = ctx.NearestClamp;
        commands.Push(GpuStage.Compute, view.Constants);

        (uint across, uint down) = Tiles(view.Rect.Width, view.Rect.Height);
        Culling.Dispatch(commands, ctx.Lighting.LightCull,
            [view.Buffers.LightTiles.Handle],
            [ctx.Lights.Handle],
            (across + CullGroup - 1) / CullGroup, (down + CullGroup - 1) / CullGroup,
            samplers: [new GpuBinding(Texture: gbuffer.Depth.Texture, Sampler: nearest)]);

        commands.BeginComputePass([new GpuBinding(view.Buffers.LightClusters.Handle)]);
        commands.BindPipeline(ctx.Lighting.LightCluster);
        commands.BindStorageBuffers(GpuStage.Compute, 0, [ctx.Lights.Handle, view.Buffers.LightTiles.Handle]);
        commands.Dispatch((across + ClusterGroup - 1) / ClusterGroup, (down + ClusterGroup - 1) / ClusterGroup, (Slices + ClusterGroup - 1) / ClusterGroup);
        commands.EndComputePass();

        // The shade reads the shadow atlas and records too, through the lighting's own constants.
        commands.Push(GpuStage.Compute, view.Shade);
        commands.BeginComputePass([new GpuBinding(Texture: targets.Hdr.Texture)]);
        commands.BindPipeline(ctx.Lighting.Shade);
        commands.BindTextures(GpuStage.Compute, 0,
        [
            new GpuBinding(Texture: gbuffer.Emissive.Texture, Sampler: nearest),
            new GpuBinding(Texture: gbuffer.Albedo.Texture, Sampler: nearest),
            new GpuBinding(Texture: gbuffer.Normal.Texture, Sampler: nearest),
            new GpuBinding(Texture: gbuffer.Material.Texture, Sampler: nearest),
            new GpuBinding(Texture: gbuffer.Depth.Texture, Sampler: nearest),
            new GpuBinding(Texture: targets.Ao.Texture, Sampler: nearest),
            new GpuBinding(Texture: Shadows.AtlasOrStub(ctx), Sampler: ctx.Shadows.Comparison),
            new GpuBinding(Texture: ctx.GiVolumes.Sh[ctx.GiVolumes.Current][0], Sampler: ctx.LinearClamp),
            new GpuBinding(Texture: ctx.GiVolumes.Sh[ctx.GiVolumes.Current][1], Sampler: ctx.LinearClamp),
            new GpuBinding(Texture: ctx.GiVolumes.Sh[ctx.GiVolumes.Current][2], Sampler: ctx.LinearClamp),
            new GpuBinding(Texture: ctx.GiVolumes.SkyVis, Sampler: ctx.LinearClamp),
            new GpuBinding(Texture: ctx.GiVolumes.Albedo, Sampler: ctx.NearestClamp),
        ]);
        commands.BindStorageBuffers(GpuStage.Compute, 0, [ctx.Lights.Handle, view.Buffers.LightTiles.Handle, view.Buffers.LightClusters.Handle, ctx.Shadows.Records.Handle]);
        commands.Dispatch((uint)(view.Rect.Width + ShadeGroup - 1) / ShadeGroup, (uint)(view.Rect.Height + ShadeGroup - 1) / ShadeGroup);
        commands.EndComputePass();

        return 3;
    }

    /// <summary>
    /// How many tiles cover a view of this size, across and down; the last ones may hang over its edge.
    /// </summary>
    private static (uint Across, uint Down) Tiles(int viewWidth, int viewHeight)
    {
        return ((uint)(Math.Max(1, viewWidth) + TileSize - 1) / TileSize, (uint)(Math.Max(1, viewHeight) + TileSize - 1) / TileSize);
    }
}
