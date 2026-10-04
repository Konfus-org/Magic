using Magic.Contexts.Components;
using Magic.Contexts.Rendering;
using Magic.Contexts.Settings;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Utils;
using System.Drawing;
using System.Numerics;

namespace DeferredRendererGem;

/// <summary>
/// The shadow maps, as commands. Each frame: the local lights nearest the camera are given pages of the atlas
/// (<see cref="SelectLocal"/>, before the lights are uploaded, since each light carries the index of its records), every
/// perspective view's cascades are fitted (<see cref="PlanCascades"/>), and then every shadow view to draw this frame is
/// culled on the GPU (one compute pass each, <see cref="RecordCull"/>) and drawn into its tile of the atlas in one
/// depth-only render pass (<see cref="RecordDraw"/>), before anything of the frame is lit. The lighting then reads the
/// atlas through the cascade constants and the record buffer (<c>Lighting/Shadows.hlsli</c>).
/// </summary>
internal static class Shadows
{
    /// <summary>
    /// A new frame: the atlas fits the settings and this many perspective views, the pipelines carry the settings' bias,
    /// last frame's shadow views are forgotten.
    /// </summary>
    public static void BeginFrame(RenderContext ctx, long frame, int perspectiveViews)
    {
        ShadowState state = ctx.Shadows;
        ShadowSettings settings = ctx.Settings.Shadows;
        state.Frame = frame;
        state.Views.Clear();
        state.RecordRows.Clear();
        state.LightRecords.Clear();
        state.LightFaces.Clear();
        state.RowsUsed = 0;
        state.KeptAny = false;
        if (!settings.Enabled)
            return;

        state.EnsureAtlas(ctx.Gpu, settings, perspectiveViews);
        if (state.Pipelines.SlopeBias != settings.SlopeBias)
            RebuildPipelines(ctx);
    }

    /// <summary>
    /// The shadow pipelines again, from the shaders as they are now and the bias settings as they are now.
    /// </summary>
    public static void RebuildPipelines(RenderContext ctx)
    {
        ShadowState state = ctx.Shadows;
        state.Pipelines.Release(ctx.Gpu);
        state.Pipelines = ShadowPipelines.Create(ctx);
    }

    /// <summary>
    /// Gives this frame's shadow-casting spot and point lights their pages: the <see cref="ShadowSettings.MaxLocalLights"/>
    /// biggest on the main camera's screen keep or take pages, the rest let theirs go. Their records are written, camera
    /// relative, and the faces to draw this frame chosen: every face of a light that just got pages, then the faces
    /// that waited longest, up to <see cref="ShadowSettings.LocalFacesPerFrame"/>. Per light of <paramref name="lights"/>
    /// the state then says which record is its first (<see cref="ShadowState.LightRecords"/>).
    /// </summary>
    public static void SelectLocal(RenderContext ctx, ReadOnlySpan<LightInstance> lights, ReadOnlySpan<View> views)
    {
        ShadowState state = ctx.Shadows;
        ShadowSettings settings = ctx.Settings.Shadows;
        for (int i = 0; i < lights.Length; i++)
        {
            state.LightRecords.Add(ShadowState.None);
            state.LightFaces.Add(0);
        }

        if (!settings.Enabled || settings.MaxLocalLights == 0 || !FirstPerspective(views, out View camera))
        {
            state.Locals.Clear();
            return;
        }

        Vector3 cameraPos = camera.World.Translation;
        float projScaleY = 1f / MathF.Tan(float.DegreesToRadians(camera.Camera.FieldOfView) * 0.5f);
        List<(float Score, int Light)> ranked = [];
        for (int i = 0; i < lights.Length; i++)
        {
            ref readonly LightInstance light = ref lights[i];
            if (light.Kind != LightKind.Directional && light.CastsShadows && light.Range > 0f)
                ranked.Add((LocalShadows.Score(light, cameraPos, projScaleY), i));
        }

        ranked.Sort(static (a, b) => b.Score.CompareTo(a.Score));
        if (ranked.Count > settings.MaxLocalLights)
            ranked.RemoveRange(settings.MaxLocalLights, ranked.Count - settings.MaxLocalLights);

        foreach (LocalShadow local in state.Locals.Values)
            local.Used = false;

        // Pages: kept by the lights still here, freed by the ones gone, then taken by the new ones.
        List<(LocalShadow Local, int Light)> chosen = [];
        List<(LocalShadow Local, int Light)> fresh = [];
        foreach ((float _, int index) in ranked)
        {
            LocalKey key = LocalKey.Of(lights[index]);
            if (state.Locals.TryGetValue(key, out LocalShadow? local))
            {
                local.Used = true;
                chosen.Add((local, index));
            }
            else
                fresh.Add((new LocalShadow { Key = key, Pages = new int[lights[index].Kind == LightKind.Point ? LocalShadows.PointFaces : 1] }, index));
        }

        foreach ((LocalKey key, LocalShadow local) in state.Locals.ToArray())
        {
            if (local.Used)
                continue;

            foreach (int page in local.Pages)
                state.FreePages.Push(page);
            state.Locals.Remove(key);
        }

        foreach ((LocalShadow local, int index) in fresh)
        {
            if (state.FreePages.Count < local.Pages.Length)
                continue; // the pool is spoken for: the light goes without shadow this frame

            for (int face = 0; face < local.Pages.Length; face++)
                local.Pages[face] = state.FreePages.Pop();
            local.Used = true;
            state.Locals[local.Key] = local;
            chosen.Add((local, index));
        }

        // Faces drawn this frame: a new light's at once, then the oldest, within the budget.
        chosen.Sort(static (a, b) => a.Local.LastDrawn.CompareTo(b.Local.LastDrawn));
        int budget = settings.LocalFacesPerFrame;
        foreach ((LocalShadow local, int index) in chosen)
        {
            ref readonly LightInstance light = ref lights[index];
            bool draw = local.LastDrawn < 0 || budget >= local.Pages.Length;
            if (draw && local.LastDrawn >= 0)
                budget -= local.Pages.Length;
            if (!draw)
                state.KeptAny = true;

            state.LightRecords[index] = (uint)state.RecordRows.Count;
            state.LightFaces[index] = (uint)local.Pages.Length;
            for (int face = 0; face < local.Pages.Length; face++)
            {
                ShadowFace view = light.Kind == LightKind.Point ? LocalShadows.Point(light, face) : LocalShadows.Spot(light);
                Rectangle tile = state.Page(local.Pages[face]);
                state.RecordRows.Add(new GpuShadowRecord
                {
                    RectUv = state.RectUv(tile),
                    ViewProj = view.ViewProj(cameraPos),
                    Params = new Vector4(2f * MathF.Tan(view.FovYRadians * 0.5f) / tile.Width, LocalShadows.Near, 0f, 0f),
                });

                if (draw)
                {
                    ShadowCullConstants cull = new() { Frame = FrameConstants.ForShadowView(view.ViewProj(view.Eye), view.Rotation, view.Eye, tile, view.Projection, LocalShadows.Near, light.Range, isOrthographic: false, ctx.Settings, state.Frame) };
                    AddView(ctx, cull, tile);
                }
            }

            if (draw)
                local.LastDrawn = state.Frame;
        }

        Debugging.Stats.Set("Shadows.LocalLights", chosen.Count);
    }

    /// <summary>
    /// The records as this frame's lights refer to them, into their buffer; after <see cref="SelectLocal"/>.
    /// </summary>
    public static void UploadRecords(RenderContext ctx)
    {
        ShadowState state = ctx.Shadows;
        if (state.RecordRows.Count == 0)
            return;

        state.Records.Ensure(ctx.Gpu, (uint)(state.RecordRows.Count * GpuShadowRecord.Size));
        ctx.Gpu.Upload<GpuShadowRecord>(state.Records.Handle, 0, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(state.RecordRows));
    }

    /// <summary>
    /// A perspective view's cascades for this frame, when the sun casts: split along the view, each fitted and snapped
    /// (<see cref="Cascades.Fit"/>), the far ones every other frame when staggered, and each refitted one made a shadow
    /// view to draw. <paramref name="buffers"/> keeps the fits.
    /// </summary>
    public static void PlanCascades(RenderContext ctx, in View view, Rectangle rect, ViewBuffers buffers, in LightingConstants lighting)
    {
        ShadowState state = ctx.Shadows;
        ShadowSettings settings = ctx.Settings.Shadows;
        bool wanted = settings.Enabled && lighting.SunCastsShadows && lighting.SunColor != Vector3.Zero && view.Camera.Projection == Projection.Perspective && state.RowsUsed < state.CascadeRows;
        if (!wanted)
        {
            if (buffers.Shadows is { } off)
                off.Count = 0;
            return;
        }

        ShadowBuffers shadows = buffers.Shadows ??= new ShadowBuffers();
        int count = settings.Cascades;
        int row = state.RowsUsed++;
        if (shadows.Row != row)
            shadows.Fitted = 0; // another row: the kept fits point at tiles that are not this view's
        shadows.Row = row;
        shadows.Count = count;
        shadows.Refreshed = 0;

        Span<float> fars = stackalloc float[count];
        float near = view.Camera.Near;
        Cascades.Split(near, MathF.Max(settings.Distance, near + 1f), settings.SplitLambda, fars);
        Matrix4x4 lightRotation = Cascades.LightRotation(lighting.SunDirection);
        float aspect = rect.Height > 0 ? (float)rect.Width / rect.Height : 1f;
        float fov = float.DegreesToRadians(view.Camera.FieldOfView);
        for (int i = 0; i < count; i++)
        {
            bool refresh = i >= shadows.Fitted || !settings.StaggerFar || i < 2 || ((state.Frame + i) & 1) == 0;
            if (!refresh)
            {
                state.KeptAny = true;
                continue;
            }

            // A cascade starts where the one before blends into it, so the blend band's pixels find their casters in both.
            float nearSplit = i == 0 ? near : fars[i - 1] * (1f - settings.BlendFraction);
            Cascade cascade = Cascades.Fit(view.World, fov, aspect, nearSplit, fars[i], lightRotation, settings.CascadeResolution, settings.CasterRange);
            shadows.Cascades[i] = cascade;
            shadows.Refreshed |= 1u << i;
            Rectangle tile = shadows.Tile(i, settings.CascadeResolution);
            Matrix4x4 projection = Matrix4x4.OrthographicReverseZ(2f * cascade.Radius, 2f * cascade.Radius, cascade.NearPlane, cascade.FarPlane);
            ShadowCullConstants cull = new() { Frame = FrameConstants.ForShadowView(cascade.ViewProj(cascade.Center), lightRotation, cascade.Center, tile, projection, cascade.NearPlane, cascade.FarPlane, isOrthographic: true, ctx.Settings, state.Frame) };
            cull.PlaneCount = (uint)Cascades.ReceiverPlanes(view.World, fov, aspect, nearSplit, fars[i], lighting.SunDirection, cascade.Center, cull.Planes);
            AddView(ctx, cull, tile);
        }

        shadows.Fitted = Math.Max(shadows.Fitted, count);
    }

    /// <summary>
    /// The culls of this frame's shadow views, one compute pass each, outside any pass. Returns the dispatches recorded.
    /// </summary>
    public static int RecordCull(RenderContext ctx, RenderCommands commands)
    {
        ShadowState state = ctx.Shadows;
        InstanceTable instances = ctx.Instances;
        foreach (ShadowView view in state.Views)
        {
            (GrowableBuffer drawArgs, GrowableBuffer visibleIds) = state.Buffers[view.Buffers];
            commands.Push(GpuStage.Compute, view.Cull);
            Culling.Dispatch(commands, state.Pipelines.Cull,
                [drawArgs.Handle, visibleIds.Handle],
                [instances.PageBuffer.Handle, instances.CellBuffer.Handle, instances.CullBuffer.Handle, ctx.Buckets.Lods.Handle],
                (uint)instances.PageCount);
        }

        return state.Views.Count;
    }

    /// <summary>
    /// The atlas: one depth-only render pass, every shadow view of the frame drawn into its tile with the one shadow
    /// pipeline, an indirect call per chunk of its draw args. Cleared whole when every tile is drawn this frame; else
    /// loaded, each drawn tile cleared first by a scissored triangle at depth 0. The stub is cleared the first time
    /// through. Returns the draws recorded.
    /// </summary>
    public static int RecordDraw(RenderContext ctx, RenderCommands commands)
    {
        ShadowState state = ctx.Shadows;
        if (!state.StubCleared)
        {
            commands.BeginDepthPass(state.Stub, GpuLoad.Clear);
            commands.EndRenderPass();
            state.StubCleared = true;
        }

        if (state.Views.Count == 0 || !state.Atlas.IsValid)
            return 0;

        bool clearAll = !state.KeptAny;
        commands.BeginDepthPass(state.Atlas, clearAll ? GpuLoad.Clear : GpuLoad.Load);
        commands.BindIndexBuffer(ctx.Meshes.IndexBuffer, wide: true);
        commands.BindStorageBuffers(GpuStage.Vertex, 0, [ctx.Instances.XformBuffer.Handle]);

        int draws = 0;
        foreach (ShadowView view in state.Views)
        {
            commands.SetViewport(view.Tile);
            commands.SetScissor(view.Tile);
            if (!clearAll)
            {
                commands.BindPipeline(state.Pipelines.Clear);
                commands.Draw(3);
                draws++;
            }

            if (ctx.Buckets.ChunkCount == 0)
                continue;

            (GrowableBuffer drawArgs, GrowableBuffer visibleIds) = state.Buffers[view.Buffers];
            commands.Push(GpuStage.Vertex, view.Cull.Frame);
            commands.BindVertexBuffers(0, [ctx.Meshes.VertexBuffer, visibleIds.Handle]);
            commands.BindPipeline(state.Pipelines.Depth);
            for (int chunk = 0; chunk < ctx.Buckets.ChunkCount; chunk++)
            {
                if (ctx.Buckets.IsEmpty(chunk))
                    continue;

                commands.DrawIndexedIndirect(drawArgs.Handle, (uint)chunk * Buckets.ChunkBytes, Buckets.GroupsPerChunk);
                draws++;
            }
        }

        commands.EndRenderPass();
        Debugging.Stats.Set("Shadows.Views", state.Views.Count);
        Debugging.Stats.Set("Shadows.Draws", draws);
        return draws;
    }

    /// <summary>
    /// The texture the lighting samples as the shadow atlas: the atlas while it exists, else the stub.
    /// </summary>
    public static GpuTexture AtlasOrStub(RenderContext ctx)
    {
        ShadowState state = ctx.Shadows;
        return state.Atlas.IsValid && ctx.Settings.Shadows.Enabled ? state.Atlas : state.Stub;
    }

    /// <summary>
    /// A shadow view to cull and draw this frame, with buffers of its own seeded from the bucket template.
    /// </summary>
    private static void AddView(RenderContext ctx, in ShadowCullConstants constants, Rectangle tile)
    {
        ShadowState state = ctx.Shadows;
        IRendering gpu = ctx.Gpu;
        int index = state.Views.Count;
        if (state.Buffers.Count <= index)
        {
            const GpuBufferUsage rw = GpuBufferUsage.ComputeRead | GpuBufferUsage.ComputeWrite;
            state.Buffers.Add((new GrowableBuffer(gpu, GpuBufferUsage.Indirect | rw, Buckets.ChunkBytes), new GrowableBuffer(gpu, GpuBufferUsage.Vertex | GpuBufferUsage.ComputeWrite, 1024)));
        }

        (GrowableBuffer drawArgs, GrowableBuffer visibleIds) = state.Buffers[index];
        uint argsBytes = Math.Max((uint)ctx.Buckets.ChunkCount * Buckets.ChunkBytes, Buckets.ChunkBytes);
        drawArgs.Ensure(gpu, argsBytes);
        visibleIds.Ensure(gpu, ctx.Buckets.VisibleHighWater * GpuVisible.Size);
        gpu.Copy(ctx.Buckets.Template.Handle, 0, drawArgs.Handle, 0, Math.Min(argsBytes, (uint)ctx.Buckets.TemplateRows.Length * DrawArgs.Size));
        state.Views.Add(new ShadowView(constants, tile, index));
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
