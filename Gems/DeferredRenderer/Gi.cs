using Magic.Contexts.Components;
using Magic.Contexts.Rendering;
using Magic.Contexts.Settings;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Mathematics;
using Magic.Utils;
using System.Numerics;

namespace DeferredRendererGem;

/// <summary>
/// The GI compute pipelines, in the order a level runs them.
/// </summary>
internal sealed record GiPipelines(
    GpuPipeline BrickClear,
    GpuPipeline BrickBuild,
    GpuPipeline CollectPages,
    GpuPipeline CollectInstances,
    GpuPipeline Stamp,
    GpuPipeline Resolve,
    GpuPipeline DistanceX,
    GpuPipeline DistanceY,
    GpuPipeline DistanceZ,
    GpuPipeline LightGrid,
    GpuPipeline Sky,
    GpuPipeline Propagate)
{
    public void Release(IRendering gpu)
    {
        foreach (GpuPipeline pipeline in (ReadOnlySpan<GpuPipeline>)[BrickClear, BrickBuild, CollectPages, CollectInstances, Stamp, Resolve, DistanceX, DistanceY, DistanceZ, LightGrid, Sky, Propagate])
            gpu.Release(pipeline);
    }
}

/// <summary>
/// The fake global illumination, as commands. Each frame one level of the clipmap is rebuilt from scratch: the
/// instances in it are stamped through their meshes' occupancy bricks into what the voxels are made of, the distance
/// field is taken, how much sky each voxel sees is traced, and the light is injected and passed on one step
/// (<c>Resources/Shaders/Gi/</c>). The finest level goes every other frame, the coarser ones in turn, so the cost is
/// one level a frame whatever the count. Before any of it, the bricks of meshes drawn for the first time are built,
/// a few a frame. The lighting then reads the volumes per pixel (<c>Include/Gi.hlsli</c>).
/// </summary>
internal static class Gi
{
    public const string BrickClearShader = "Gi/BrickClear.comp.hlsl";
    public const string BrickBuildShader = "Gi/BrickBuild.comp.hlsl";
    public const string CollectPagesShader = "Gi/CollectPages.comp.hlsl";
    public const string CollectInstancesShader = "Gi/CollectInstances.comp.hlsl";
    public const string StampShader = "Gi/Stamp.comp.hlsl";
    public const string ResolveShader = "Gi/Resolve.comp.hlsl";
    public const string DistanceShader = "Gi/Distance.comp.hlsl";
    public const string LightGridShader = "Gi/LightGrid.comp.hlsl";
    public const string SkyShader = "Gi/Sky.comp.hlsl";
    public const string PropagateShader = "Gi/Propagate.comp.hlsl";

    public static readonly string[] AllShaders = [BrickClearShader, BrickBuildShader, CollectPagesShader, CollectInstancesShader, StampShader, ResolveShader, DistanceShader, LightGridShader, SkyShader, PropagateShader];

    public static GiPipelines Create(RenderContext ctx)
    {
        IRendering gpu = ctx.Gpu;
        GpuPipeline Compute(string path, string defines = "") => gpu.CreateComputePipeline(Shaders.CompileBuiltIn(ctx, path, GpuStage.Compute, defines));

        return new GiPipelines(
            Compute(BrickClearShader),
            Compute(BrickBuildShader),
            Compute(CollectPagesShader),
            Compute(CollectInstancesShader),
            Compute(StampShader),
            Compute(ResolveShader),
            Compute(DistanceShader, "#define AXIS 0\n"),
            Compute(DistanceShader, "#define AXIS 1\n"),
            Compute(DistanceShader, "#define AXIS 2\n"),
            Compute(LightGridShader),
            Compute(SkyShader),
            Compute(PropagateShader));
    }

    public static void RebuildPipelines(RenderContext ctx)
    {
        ctx.Gi.Release(ctx.Gpu);
        ctx.Gi = Create(ctx);
    }

    /// <summary>
    /// Before the frame is planned: the volumes fit the settings, every level's origin follows the main camera, the level
    /// to rebuild this frame is chosen and the buffers it counts into are reset. Nothing when the GI is off or there is no
    /// perspective camera.
    /// </summary>
    public static void Plan(RenderContext ctx, ReadOnlySpan<View> views, long frame)
    {
        GiSettings settings = ctx.Settings.Gi;
        GiVolumes volumes = ctx.GiVolumes;
        volumes.UpdateLevel = -1;
        if (!settings.Enabled || !FirstPerspective(views, out View camera))
        {
            volumes.EnsureStub(ctx.Gpu); // the lighting binds the volumes whatever the setting
            return;
        }

        volumes.Ensure(ctx.Gpu, settings);
        Vector3 cameraPos = camera.World.Translation;
        int level = GiClipmap.UpdateLevel(frame, settings.Levels);
        for (int i = 0; i < settings.Levels; i++)
        {
            float voxel = GiClipmap.VoxelSize(settings.VoxelSize, settings.LevelScale, i);
            Vector3 origin = GiClipmap.Origin(cameraPos, settings.Resolution, voxel);
            if (i == level)
            {
                volumes.Shift = (volumes.Valid & (1u << i)) != 0 ? GiClipmap.Shift(volumes.Origins[i], origin, voxel) : Vector3.Zero;
                volumes.Origins[i] = origin;
            }
            else if ((volumes.Valid & (1u << i)) == 0)
                volumes.Origins[i] = origin;

            volumes.Voxels[i] = voxel;
        }

        volumes.UpdateLevel = level;
        IRendering gpu = ctx.Gpu;
        volumes.PageList.Ensure(gpu, Math.Max(16, (uint)ctx.Instances.PageCount * 4));
        gpu.Upload<uint>(volumes.PageArgs, 0, [0, 1, 1, (uint)ctx.Instances.PageCount]);
        gpu.Upload<uint>(volumes.TaskArgs, 0, [0, 1, 1, 0]);
        gpu.Upload<uint>(volumes.Counters, 0, [0, 0, 0, 0]);
    }

    /// <summary>
    /// Fills the clipmap's part of the lighting constants.
    /// </summary>
    public static void Fill(RenderContext ctx, ref ShadeConstants constants)
    {
        GiSettings settings = ctx.Settings.Gi;
        GiVolumes volumes = ctx.GiVolumes;
        if (!settings.Enabled || !volumes.Made)
            return;

        constants.GiLevels = (uint)volumes.Levels;
        constants.GiResolution = (uint)volumes.Resolution;
        constants.GiFlags = ShadeConstants.GiEnabledFlag | (volumes.Valid << 8);
        constants.GiUpdateLevel = (uint)Math.Max(0, volumes.UpdateLevel);
        constants.GiInteriorTint = new Vector4(settings.InteriorTint * settings.InteriorStrength, settings.Intensity);
        constants.SkyColor = new Vector4(constants.Frame.Ambient, settings.FadeVoxels);
        constants.GiShift = new Vector4(volumes.Shift, settings.PropagationDamping);
        for (int i = 0; i < volumes.Levels; i++)
        {
            float voxel = volumes.Voxels[i];
            constants.ClipmapOrigin[i] = new Vector4(volumes.Origins[i], voxel);
            constants.ClipmapCameraOffset[i] = new Vector4(constants.Frame.CameraPos - volumes.Origins[i], 1f / (voxel * volumes.Resolution));
        }
    }

    /// <summary>
    /// This frame's GI work, outside any pass, before anything is lit: the bricks due, then the level chosen by
    /// <see cref="Plan"/>. Returns the dispatches recorded.
    /// </summary>
    public static int Record(RenderContext ctx, RenderCommands commands, in ShadeConstants shade)
    {
        GiVolumes volumes = ctx.GiVolumes;
        if (volumes.UpdateLevel < 0 || !volumes.Made)
            return 0;

        int dispatches = RecordBricks(ctx, commands, shade.Frame);
        GiPipelines pipelines = ctx.Gi;
        InstanceTable instances = ctx.Instances;
        uint res = (uint)volumes.Resolution;
        uint groups = (res + 3) / 4;
        uint lines = (res + 7) / 8;
        GpuSampler nearest = ctx.NearestClamp, linear = ctx.LinearClamp;
        int reading = volumes.Current, writing = volumes.Current ^ 1;
        commands.Push(GpuStage.Compute, shade);

        Culling.Dispatch(commands, pipelines.CollectPages,
            [volumes.PageList.Handle, volumes.PageArgs],
            [instances.PageBuffer.Handle, instances.CellBuffer.Handle],
            ((uint)instances.PageCount + 63) / 64);

        Culling.Dispatch(commands, pipelines.CollectInstances,
            [volumes.Accum, volumes.Tasks, volumes.TaskArgs, volumes.Counters],
            [volumes.PageList.Handle, instances.PageBuffer.Handle, instances.CullBuffer.Handle, instances.XformBuffer.Handle, ctx.Buckets.GiGroups.Handle, ctx.Materials.GiRecords.Handle],
            0, indirect: volumes.PageArgs);

        Culling.Dispatch(commands, pipelines.Stamp,
            [volumes.Accum],
            [volumes.Tasks, instances.CullBuffer.Handle, instances.XformBuffer.Handle, ctx.Buckets.GiGroups.Handle, ctx.Materials.GiRecords.Handle],
            0, indirect: volumes.TaskArgs,
            samplers: [new GpuBinding(Texture: volumes.BrickAtlas, Sampler: nearest)]);

        commands.BeginComputePass([new GpuBinding(volumes.Accum), new GpuBinding(volumes.LightGrid), new GpuBinding(Texture: volumes.Albedo), new GpuBinding(Texture: volumes.Emissive)]);
        commands.BindPipeline(pipelines.Resolve);
        commands.Dispatch(groups, groups, groups);
        commands.EndComputePass();

        Volume(commands, pipelines.DistanceX, volumes.ScratchA, [new GpuBinding(Texture: volumes.Albedo, Sampler: nearest)], lines, lines, 1);
        Volume(commands, pipelines.DistanceY, volumes.ScratchB, [new GpuBinding(Texture: volumes.ScratchA, Sampler: nearest)], lines, lines, 1);
        Volume(commands, pipelines.DistanceZ, volumes.Sdf, [new GpuBinding(Texture: volumes.ScratchB, Sampler: nearest)], lines, lines, 1);

        Culling.Dispatch(commands, pipelines.LightGrid, [volumes.LightGrid], [ctx.Lights.Handle], (shade.Frame.LightCount + 63) / 64);

        Volume(commands, pipelines.Sky, volumes.SkyVis,
            [new GpuBinding(Texture: volumes.Albedo, Sampler: nearest), new GpuBinding(Texture: volumes.Sdf, Sampler: linear)], groups, groups, groups);

        GpuTexture[] old = volumes.Sh[reading], fresh = volumes.Sh[writing];
        commands.BeginComputePass([new GpuBinding(Texture: fresh[0]), new GpuBinding(Texture: fresh[1]), new GpuBinding(Texture: fresh[2])]);
        commands.BindPipeline(pipelines.Propagate);
        commands.BindTextures(GpuStage.Compute, 0,
        [
            new GpuBinding(Texture: volumes.Albedo, Sampler: nearest),
            new GpuBinding(Texture: volumes.Emissive, Sampler: nearest),
            new GpuBinding(Texture: old[0], Sampler: nearest),
            new GpuBinding(Texture: old[1], Sampler: nearest),
            new GpuBinding(Texture: old[2], Sampler: nearest),
            new GpuBinding(Texture: Shadows.AtlasOrStub(ctx), Sampler: ctx.Shadows.Comparison),
        ]);
        commands.BindStorageBuffers(GpuStage.Compute, 0, [ctx.Lights.Handle, volumes.LightGrid, ctx.Shadows.Records.Handle]);
        commands.Dispatch(groups, groups, groups);
        commands.EndComputePass();

        // The other levels' light is still in the old set: copied over so the new set holds every level.
        CopyOtherLevels(ctx, old, fresh, volumes);
        volumes.Current = writing;
        volumes.Valid |= 1u << volumes.UpdateLevel;

        Debugging.Stats.Set("Gi.Level", volumes.UpdateLevel);
        Debugging.Stats.Set("Gi.Bricks", ctx.Bricks.Count);
        Debugging.Stats.Set("Gi.BricksPending", ctx.Bricks.Pending.Count);
        Debugging.Stats.Set("Gi.BricksDropped", ctx.Bricks.Dropped);
        return dispatches + 11;
    }

    /// <summary>
    /// The bricks due this frame: each cleared, then rasterised from its mesh's triangles.
    /// </summary>
    private static int RecordBricks(RenderContext ctx, RenderCommands commands, in FrameConstants frame)
    {
        GiVolumes volumes = ctx.GiVolumes;
        GiBricks bricks = ctx.Bricks;
        int budget = ctx.Settings.Gi.BricksPerFrame;
        int dispatches = 0;
        while (budget-- > 0 && bricks.Pending.TryDequeue(out uint meshSlot))
        {
            uint brick = bricks.BrickOf(meshSlot);
            if (brick == GiBricks.None)
                continue; // released before it was built

            (uint firstIndex, uint indexCount, int vertexOffset) = ctx.Meshes.Range(meshSlot);
            Aabb box = GiVolumes.InflateFlat(ctx.Meshes.Box(meshSlot));
            BrickConstants constants = new()
            {
                Frame = frame,
                BoxMin = new Vector4(box.Min, 0f),
                BoxMax = new Vector4(box.Max, 0f),
                Job = new UintVector4(firstIndex, indexCount, (uint)vertexOffset, brick),
            };
            commands.Push(GpuStage.Compute, constants);
            Volume(commands, ctx.Gi.BrickClear, volumes.BrickAtlas, [], (GiVolumes.BrickSize * GiVolumes.BrickSize * GiVolumes.BrickSize) / 64, 1, 1);

            commands.BeginComputePass([new GpuBinding(Texture: volumes.BrickAtlas)]);
            commands.BindPipeline(ctx.Gi.BrickBuild);
            commands.BindStorageBuffers(GpuStage.Compute, 0, [ctx.Meshes.VertexBuffer, ctx.Meshes.IndexBuffer]);
            commands.Dispatch(((indexCount / 3) + 63) / 64);
            commands.EndComputePass();
            dispatches += 2;
        }

        return dispatches;
    }

    /// <summary>
    /// One compute pass writing one volume from sampled textures.
    /// </summary>
    private static void Volume(RenderCommands commands, GpuPipeline pipeline, GpuTexture target, ReadOnlySpan<GpuBinding> samplers, uint x, uint y, uint z)
    {
        commands.BeginComputePass([new GpuBinding(Texture: target)]);
        commands.BindPipeline(pipeline);
        if (!samplers.IsEmpty)
            commands.BindTextures(GpuStage.Compute, 0, samplers);
        commands.Dispatch(x, y, z);
        commands.EndComputePass();
    }

    /// <summary>
    /// The levels not rebuilt this frame keep their light: their slabs are copied from the set read to the set written.
    /// </summary>
    private static void CopyOtherLevels(RenderContext ctx, GpuTexture[] old, GpuTexture[] fresh, GiVolumes volumes)
    {
        uint res = (uint)volumes.Resolution;
        for (int level = 0; level < volumes.Levels; level++)
        {
            if (level == volumes.UpdateLevel || (volumes.Valid & (1u << level)) == 0)
                continue;

            for (int channel = 0; channel < 3; channel++)
            {
                TextureRegion source = new(old[channel], Z: (uint)level * res, Depth: res);
                TextureRegion destination = new(fresh[channel], Z: (uint)level * res, Depth: res);
                ctx.Gpu.Copy(source, destination);
            }
        }
    }

    private static bool FirstPerspective(ReadOnlySpan<View> views, out View found)
    {
        foreach (View view in views)
        {
            if (view.Camera.Projection == Projection.Perspective)
            {
                found = view;
                return true;
            }
        }

        found = default;
        return false;
    }
}
