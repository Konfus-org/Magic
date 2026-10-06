using Magic.Contexts.Assets;
using Magic.Contexts.Rendering;
using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;

namespace DeferredRendererGem;

/// <summary>
/// Records the passes of a stage as commands, the one way every pass runs: for each pass that runs this frame, for each
/// thing its stage runs over (the frame, a render target, a view), for each of its iterations, its constants pushed
/// (the view's frame block, its packed parameters, the iteration), the resources it names bound in the order written
/// and its dispatch or draw. Outside any pass. Changes nothing but which of a ping-ponged target's two textures is the
/// current one. A name that stands for nothing this frame skips the pass, said once.
/// </summary>
internal static class PassExecutor
{
    private const uint VerticesPerQuad = 6; // two triangles from the vertex id alone
    private const int MaxBindings = 32;

    /// <summary>
    /// What a draw that clears clears its colours to: nothing. Where nothing is drawn shows what the sky stage paints.
    /// </summary>
    private static readonly Vector4 GBufferClear = new(0f, 0f, 0f, 1f);

    /// <summary>
    /// A frame-wide stage (shadows, GI), with the main view's constants. Returns the draws and dispatches recorded.
    /// </summary>
    public static (int Draws, int Dispatches) RecordFrame(RenderContext ctx, RenderCommands commands, FramePlan plan, PipelineStage stage)
    {
        (int draws, int dispatches) = (0, 0);
        if (plan.Views.Count == 0)
            return (draws, dispatches);

        ViewPlan main = plan.Views[plan.MainView];
        ResourceScope scope = new(ctx, null, null);
        foreach (PassState pass in ctx.Pipeline.Stages[(int)stage])
        {
            if (!ctx.Pipeline.Runs(pass, plan.Counts))
                continue;

            switch (pass.Pass.Kind)
            {
                case PassKind.Draw:
                    draws += RecordShadowDraw(ctx, commands, pass, scope, main.Constants, plan.Counts);
                    break;
                case PassKind.Quads:
                    draws += RecordQuads(ctx, commands, pass, scope, plan.Counts, [main]);
                    break;
                case PassKind.Compute or PassKind.Fullscreen:
                    RecordIterations(ctx, commands, pass, scope, main.Constants, plan.Counts, ref draws, ref dispatches);
                    break;
            }
        }

        return (draws, dispatches);
    }

    /// <summary>
    /// A stage over one render target: per view for a view stage, once for a target stage. True when a pass wrote Ldr.
    /// </summary>
    public static bool RecordTarget(
        RenderContext ctx,
        RenderCommands commands,
        FramePlan plan,
        FrameTargets targets,
        ReadOnlySpan<ViewPlan> views,
        PipelineStage stage,
        ref int draws,
        ref int dispatches)
    {
        bool wroteLdr = false;
        if (views.Length == 0)
            return false;

        bool perView = PassState.ScopeOf(stage) == PassScope.View;
        foreach (PassState pass in ctx.Pipeline.Stages[(int)stage])
        {
            if (!ctx.Pipeline.Runs(pass, plan.Counts))
                continue;

            switch (pass.Pass.Kind)
            {
                case PassKind.Draw:
                    draws += RecordCameraDraw(ctx, commands, pass, targets, views, GBufferClear);
                    break;
                case PassKind.Quads:
                    draws += RecordQuads(ctx, commands, pass, new ResourceScope(ctx, targets, null), plan.Counts, views);
                    break;
                default:
                    if (perView)
                    {
                        foreach (ref readonly ViewPlan view in views)
                            RecordIterations(ctx, commands, pass, new ResourceScope(ctx, targets, view.Resources), view.Constants, plan.Counts.WithTarget(targets.Width, targets.Height).WithView(view.Rect, 0), ref draws, ref dispatches);
                    }
                    else
                        RecordIterations(ctx, commands, pass, new ResourceScope(ctx, targets, null), views[0].Constants, plan.Counts.WithTarget(targets.Width, targets.Height), ref draws, ref dispatches);

                    wroteLdr |= WritesName(pass.Pass, "Ldr");
                    break;
            }
        }

        return wroteLdr;
    }

    private static void RecordIterations(
        RenderContext ctx,
        RenderCommands commands,
        PassState pass,
        in ResourceScope scope,
        in FrameConstants frame,
        in FrameCounts counts,
        ref int draws,
        ref int dispatches)
    {
        uint iterations = Iterations(pass.Pass, counts);
        for (uint iteration = 0; iteration < iterations; iteration++)
        {
            PassConstants constants = PassConstants.Of(frame);
            constants.SetParams(pass.Params);
            constants.SetIteration(iteration, iterations);
            bool recorded = pass.Pass.Kind == PassKind.Compute
                ? RecordCompute(ctx, commands, pass, scope, constants, counts, iteration)
                : RecordFullscreen(ctx, commands, pass, scope, constants);
            if (!recorded)
                return;

            if (pass.Pass.Kind == PassKind.Compute)
                dispatches++;
            else
                draws++;
        }

        // What was written into a twin is now the target's current texture.
        if (!pass.PingPong)
            return;

        foreach (PassWrite write in pass.Pass.Writes)
        {
            if (ReadsName(pass.Pass, write.Name) && scope.Target(write.Name) is { Twin.IsValid: true } target)
                FrameTargets.Swap(target);
        }
    }

    private static bool RecordCompute(RenderContext ctx, RenderCommands commands, PassState pass, in ResourceScope scope, in PassConstants constants, in FrameCounts counts, uint iteration)
    {
        if (pass.Compiled is not { } shader)
            return false;

        Span<GpuBinding> writes = stackalloc GpuBinding[MaxBindings];
        Span<GpuBinding> sampled = stackalloc GpuBinding[MaxBindings];
        Span<GpuBuffer> buffers = stackalloc GpuBuffer[MaxBindings];
        if (!BindWrites(ctx, pass, scope, writes, out int writeCount) || !BindReads(ctx, pass, scope, sampled, buffers, out int sampledCount, out int bufferCount))
            return false;

        if (!Groups(ctx, pass, scope, shader, counts, out (uint X, uint Y, uint Z) groups, out GpuBuffer indirect))
            return false;

        commands.Push(GpuStage.Compute, constants);
        commands.BeginComputePass(writes[..writeCount]);
        commands.BindPipeline(pass.Pipeline);
        if (sampledCount > 0)
            commands.BindTextures(GpuStage.Compute, 0, sampled[..sampledCount]);
        if (bufferCount > 0)
            commands.BindStorageBuffers(GpuStage.Compute, 0, buffers[..bufferCount]);

        if (indirect.IsValid)
            commands.DispatchIndirect(indirect, iteration * pass.Pass.Dispatch.IndirectStride);
        else
            commands.Dispatch(groups.X, groups.Y, groups.Z);
        commands.EndComputePass();
        return true;
    }

    private static bool RecordFullscreen(RenderContext ctx, RenderCommands commands, PassState pass, in ResourceScope scope, in PassConstants constants)
    {
        PassWrite write = pass.Pass.Writes[0];
        if (!WriteTexture(pass, scope, write.Name, out GpuTexture output))
            return Skip(ctx, pass, write.Name);

        Span<GpuBinding> sampled = stackalloc GpuBinding[MaxBindings];
        Span<GpuBuffer> buffers = stackalloc GpuBuffer[MaxBindings];
        if (!BindReads(ctx, pass, scope, sampled, buffers, out int sampledCount, out int bufferCount))
            return false;

        commands.Push(GpuStage.Fragment, constants);
        commands.BeginRenderPass(output, write.Load);
        commands.BindPipeline(pass.Pipeline);
        if (sampledCount > 0)
            commands.BindTextures(GpuStage.Fragment, 0, sampled[..sampledCount]);
        if (bufferCount > 0)
            commands.BindStorageBuffers(GpuStage.Fragment, 0, buffers[..bufferCount]);
        commands.Draw(3);
        commands.EndRenderPass();
        return true;
    }

    /// <summary>
    /// The scene's meshes into the target, every view of it in one render pass: for every material pipeline class, one
    /// indirect call per chunk of the view's draw args, the vertex stage reading each instance's slot from the visible
    /// list through the instance-rate buffer. Nothing here knows how many instances there are.
    /// </summary>
    private static int RecordCameraDraw(RenderContext ctx, RenderCommands commands, PassState pass, FrameTargets targets, ReadOnlySpan<ViewPlan> views, Vector4 clearColor)
    {
        PassDraw draw = pass.Pass.Draw;
        ResourceScope targetScope = new(ctx, targets, null);
        Span<GpuTexture> colors = stackalloc GpuTexture[4];
        if (!Colors(ctx, pass, targetScope, colors, out int colorCount, out GpuTexture depth))
            return 0;

        int draws = 0;
        bool transparent = draw.Pipeline != DrawPipeline.Material;
        bool refractive = draw.Pipeline == DrawPipeline.Refractive;
        Span<GpuBuffer> vertexBuffers = stackalloc GpuBuffer[2];
        Span<GpuBuffer> fragmentBuffers = stackalloc GpuBuffer[MaxBindings];
        Span<GpuBinding> textures = stackalloc GpuBinding[TextureTable.Classes + MaxBindings];
        commands.BeginRenderPass(colors[..colorCount], draw.Load, depth, clearColor);
        foreach (ref readonly ViewPlan view in views)
        {
            if (ctx.Buckets.ChunkCount == 0)
                break;

            ResourceScope scope = new(ctx, targets, view.Resources);
            if (!scope.TryBuffer(draw.Args, out GpuBuffer args, out _) || !scope.TryBuffer(draw.Instances, out GpuBuffer instances, out _))
            {
                Skip(ctx, pass, draw.Args);
                break;
            }

            // The mesh vertex stage reads the transforms and the cull rows; what the pass reads is the fragment stage's,
            // after the texture pools and the materials (Templates/Forward.frag.hlsl).
            vertexBuffers[0] = ctx.Instances.XformBuffer.Handle;
            vertexBuffers[1] = ctx.Instances.CullBuffer.Handle;
            fragmentBuffers[0] = ctx.Materials.Records.Handle;
            if (!BindReads(ctx, pass, scope, textures[TextureTable.Classes..], fragmentBuffers[1..], out int sampledCount, out int bufferCount))
                break;

            PassConstants constants = PassConstants.Of(view.Constants);
            constants.SetParams(pass.Params);
            constants.SetLayers(draw.Layers);
            commands.SetViewport(view.Rect);
            commands.SetScissor(view.Rect);
            commands.Push(GpuStage.Vertex, constants);
            commands.Push(GpuStage.Fragment, constants);
            commands.BindVertexBuffers(0, [ctx.Meshes.VertexBuffer, instances]);
            commands.BindIndexBuffer(ctx.Meshes.IndexBuffer, wide: true);
            commands.BindStorageBuffers(GpuStage.Vertex, 0, vertexBuffers);
            Textures.Bindings(ctx, textures);
            commands.BindTextures(GpuStage.Fragment, 0, textures[..(TextureTable.Classes + sampledCount)]);
            commands.BindStorageBuffers(GpuStage.Fragment, 0, fragmentBuffers[..(1 + bufferCount)]);

            foreach ((PipelineClass cls, List<int> chunks) in ctx.Buckets.ByClass)
            {
                if (chunks.Count == 0 || cls.Variant.HasFlag(SurfaceVariant.Transparent) != transparent || cls.Variant.HasFlag(SurfaceVariant.Refractive) != refractive)
                    continue;

                GpuPipeline pipeline = Pipelines.Get(ctx, cls); // none only while the class's first compile is still running
                if (!pipeline.IsValid)
                    continue;

                commands.BindPipeline(pipeline);
                draws += DrawChunks(ctx, commands, args, 0, CollectionsMarshal.AsSpan(chunks));
            }
        }

        commands.EndRenderPass();
        return draws;
    }

    /// <summary>
    /// The shadow atlas: one depth-only render pass, each iteration a shadow view the vertex shader reads from the
    /// pass's views buffer, drawn with the pass's depth pipeline from its own slice of the draw args.
    /// </summary>
    private static int RecordShadowDraw(RenderContext ctx, RenderCommands commands, PassState pass, in ResourceScope scope, in FrameConstants frame, in FrameCounts counts)
    {
        PassDraw draw = pass.Pass.Draw;
        if (!scope.TryTexture(draw.Depth, out GpuTexture atlas, out _, out _) || !scope.TrySize(draw.Depth, out uint width, out uint height, out _, out _))
            return Skip(ctx, pass, draw.Depth) ? 1 : 0;
        if (!scope.TryBuffer(draw.Args, out GpuBuffer args, out uint argsSlice) || !scope.TryBuffer(draw.Instances, out GpuBuffer instances, out _))
            return Skip(ctx, pass, draw.Args) ? 1 : 0;

        Span<GpuBuffer> buffers = stackalloc GpuBuffer[MaxBindings];
        buffers[0] = ctx.Instances.XformBuffer.Handle;
        if (!BufferReads(ctx, pass, scope, buffers[1..], out int extra))
            return 0;

        uint iterations = Iterations(pass.Pass, counts);
        int draws = 0;
        Rectangle whole = new(0, 0, (int)width, (int)height);
        commands.BeginDepthPass(atlas, draw.Load);
        commands.SetViewport(whole);
        commands.SetScissor(whole);
        commands.BindIndexBuffer(ctx.Meshes.IndexBuffer, wide: true);
        commands.BindStorageBuffers(GpuStage.Vertex, 0, buffers[..(1 + extra)]);
        commands.BindVertexBuffers(0, [ctx.Meshes.VertexBuffer, instances]);
        commands.BindPipeline(pass.Pipeline);
        if (draw.Impostors)
        {
            Span<GpuBinding> pools = stackalloc GpuBinding[TextureTable.Classes];
            Textures.Bindings(ctx, pools);
            commands.BindTextures(GpuStage.Fragment, 0, pools);
        }

        // The chunks of the classes this draw is for: the impostors' cards, or everything else.
        ReadOnlySpan<int> chunks = ctx.Buckets.ChunksOf(draw.Impostors);
        for (uint iteration = 0; iteration < iterations && chunks.Length > 0; iteration++)
        {
            PassConstants constants = PassConstants.Of(frame);
            constants.SetParams(pass.Params);
            constants.SetIteration(iteration, iterations);
            commands.Push(GpuStage.Vertex, constants);
            draws += DrawChunks(ctx, commands, args, iteration * argsSlice, chunks);
        }

        commands.EndRenderPass();
        return draws;
    }

    /// <summary>
    /// The draw args of <paramref name="chunks"/> (ascending), from <paramref name="offset"/> in <paramref name="args"/>:
    /// one indirect call per run of consecutive chunks that hold a group, so a class whose chunks were made one after
    /// another is a single call however many it has. Each call costs the CPU far more than its 64 commands cost the GPU
    /// (a released group's command draws nothing). Returns how many calls it made.
    /// </summary>
    private static int DrawChunks(RenderContext ctx, RenderCommands commands, GpuBuffer args, uint offset, ReadOnlySpan<int> chunks)
    {
        int draws = 0;
        int first = -1;
        int count = 0;
        foreach (int chunk in chunks)
        {
            bool empty = ctx.Buckets.IsEmpty(chunk);
            if (count > 0 && (empty || chunk != first + count))
            {
                commands.DrawIndexedIndirect(args, offset + ((uint)first * Buckets.ChunkBytes), (uint)(count * Buckets.GroupsPerChunk));
                draws++;
                count = 0;
            }

            if (empty)
                continue;

            if (count == 0)
                first = chunk;
            count++;
        }

        if (count > 0)
        {
            commands.DrawIndexedIndirect(args, offset + ((uint)first * Buckets.ChunkBytes), (uint)(count * Buckets.GroupsPerChunk));
            draws++;
        }

        return draws;
    }

    /// <summary>
    /// Quads from the vertex id alone, into the pass's targets: one draw per view, six vertices per row.
    /// </summary>
    private static int RecordQuads(RenderContext ctx, RenderCommands commands, PassState pass, in ResourceScope scope, in FrameCounts counts, ReadOnlySpan<ViewPlan> views)
    {
        PassDraw draw = pass.Pass.Draw;
        uint rows = counts.TryResolve(draw.Rows, out uint count) ? count : uint.TryParse(draw.Rows, out uint fixedRows) ? fixedRows : 0;
        if (rows == 0)
            return 0;

        Span<GpuTexture> colors = stackalloc GpuTexture[4];
        if (!Colors(ctx, pass, scope, colors, out int colorCount, out GpuTexture depth))
            return 0;

        Span<GpuBuffer> buffers = stackalloc GpuBuffer[MaxBindings];
        if (!BufferReads(ctx, pass, scope, buffers, out int bufferCount))
            return 0;

        int draws = 0;
        if (colorCount > 0)
            commands.BeginRenderPass(colors[..colorCount], draw.Load, depth);
        else
            commands.BeginDepthPass(depth, draw.Load);

        foreach (ref readonly ViewPlan view in views)
        {
            PassConstants constants = PassConstants.Of(view.Constants);
            constants.SetParams(pass.Params);
            if (colorCount > 0)
            {
                commands.SetViewport(view.Rect);
                commands.SetScissor(view.Rect);
            }

            commands.Push(GpuStage.Vertex, constants);
            commands.Push(GpuStage.Fragment, constants);
            commands.BindPipeline(pass.Pipeline);
            commands.BindStorageBuffers(GpuStage.Vertex, 0, buffers[..bufferCount]);
            commands.Draw(rows * VerticesPerQuad);
            draws++;
        }

        commands.EndRenderPass();
        return draws;
    }

    private static bool Colors(RenderContext ctx, PassState pass, in ResourceScope scope, Span<GpuTexture> colors, out int count, out GpuTexture depth)
    {
        PassDraw draw = pass.Pass.Draw;
        count = 0;
        depth = default;
        foreach (string name in draw.Colors)
        {
            if (!scope.TryTexture(name, out colors[count++], out _, out _))
                return Skip(ctx, pass, name);
        }

        if (draw.Depth.Length > 0 && !scope.TryTexture(draw.Depth, out depth, out _, out _))
            return Skip(ctx, pass, draw.Depth);

        return true;
    }

    /// <summary>
    /// What a compute pass writes, textures first then buffers, as SDL binds them.
    /// </summary>
    private static bool BindWrites(RenderContext ctx, PassState pass, in ResourceScope scope, Span<GpuBinding> writes, out int count)
    {
        count = 0;
        foreach (PassWrite write in pass.Pass.Writes)
        {
            if (!IsBuffer(write.Name, scope))
            {
                if (!WriteTexture(pass, scope, write.Name, out GpuTexture texture))
                    return Skip(ctx, pass, write.Name);
                writes[count++] = new GpuBinding(Texture: texture, Level: write.Level, Layer: write.Layer);
            }
        }

        foreach (PassWrite write in pass.Pass.Writes)
        {
            if (IsBuffer(write.Name, scope))
            {
                if (!scope.TryBuffer(write.Name, out GpuBuffer buffer, out _))
                    return Skip(ctx, pass, write.Name);
                writes[count++] = new GpuBinding(buffer);
            }
        }

        return true;
    }

    /// <summary>
    /// What a pass reads, by class in the order written: textures with their samplers, then buffers.
    /// </summary>
    private static bool BindReads(
        RenderContext ctx,
        PassState pass,
        in ResourceScope scope,
        Span<GpuBinding> sampled,
        Span<GpuBuffer> buffers,
        out int sampledCount,
        out int bufferCount)
    {
        (sampledCount, bufferCount) = (0, 0);
        foreach (PassRead read in pass.Pass.Reads)
        {
            if (IsBuffer(read.Name, scope))
            {
                if (!scope.TryBuffer(read.Name, out buffers[bufferCount++], out _))
                    return Skip(ctx, pass, read.Name);
                continue;
            }

            if (!scope.TryTexture(read.Name, out GpuTexture texture, out GpuFormat format, out bool isDepth))
                return Skip(ctx, pass, read.Name);

            sampled[sampledCount++] = new GpuBinding(Texture: texture, Sampler: Sampler(ctx, read.Filter, format, isDepth));
        }

        return true;
    }

    private static bool BufferReads(RenderContext ctx, PassState pass, in ResourceScope scope, Span<GpuBuffer> buffers, out int count)
    {
        count = 0;
        foreach (PassRead read in pass.Pass.Reads)
        {
            if (!IsBuffer(read.Name, scope))
                continue;
            if (!scope.TryBuffer(read.Name, out buffers[count++], out _))
                return Skip(ctx, pass, read.Name);
        }

        return true;
    }

    private static bool Groups(RenderContext ctx, PassState pass, in ResourceScope scope, CompiledShader shader, in FrameCounts counts, out (uint X, uint Y, uint Z) groups, out GpuBuffer indirect)
    {
        PassDispatch dispatch = pass.Pass.Dispatch;
        groups = (1, 1, 1);
        indirect = default;
        uint threadsX = Math.Max(1, shader.ThreadCountX), threadsY = Math.Max(1, shader.ThreadCountY), threadsZ = Math.Max(1, shader.ThreadCountZ);
        if (dispatch.Indirect.Length > 0)
            return scope.TryBuffer(dispatch.Indirect, out indirect, out _) || Skip(ctx, pass, dispatch.Indirect);

        if (dispatch.Groups.Length > 0)
        {
            groups = (dispatch.Groups[0], dispatch.Groups.Length > 1 ? dispatch.Groups[1] : 1, dispatch.Groups.Length > 2 ? dispatch.Groups[2] : 1);
            return true;
        }

        string per = dispatch.Per;
        if (per == "view")
        {
            uint viewWidth = counts.ViewWidth > 0 ? counts.ViewWidth : counts.TargetWidth, viewHeight = counts.ViewHeight > 0 ? counts.ViewHeight : counts.TargetHeight;
            uint deep = dispatch.Depth > 0 ? Ceil(dispatch.Depth, threadsZ) : 1;
            groups = dispatch.Tile > 0 ? (Ceil(viewWidth, dispatch.Tile), Ceil(viewHeight, dispatch.Tile), deep) : (Ceil(viewWidth, threadsX), Ceil(viewHeight, threadsY), deep);
            return true;
        }

        if (counts.TryResolve(per, out uint count))
        {
            groups = (Ceil(count, threadsX), 1, 1);
            return true;
        }

        bool oneLevel = per.StartsWith("level:", StringComparison.Ordinal);
        string name = per[(per.IndexOf(':') + 1)..];
        if (!scope.TrySize(name, out uint width, out uint height, out uint depth, out uint levels))
            return Skip(ctx, pass, name);

        uint extent = dispatch.Depth > 0 ? dispatch.Depth : oneLevel ? depth / Math.Max(1, levels) : depth;
        groups = (Ceil(width, threadsX), Ceil(height, threadsY), Ceil(extent, threadsZ));
        return true;
    }

    /// <summary>
    /// The texture a pass writes under <paramref name="name"/>: a ping-ponged target's twin, else the texture itself.
    /// </summary>
    private static bool WriteTexture(PassState pass, in ResourceScope scope, string name, out GpuTexture texture)
    {
        if (pass.PingPong && ReadsName(pass.Pass, name) && scope.Target(name) is { Twin.IsValid: true } target)
        {
            texture = target.Twin;
            return true;
        }

        return scope.TryTexture(name, out texture, out _, out _);
    }

    private static GpuSampler Sampler(RenderContext ctx, ReadFilter filter, GpuFormat format, bool isDepth)
    {
        return filter switch
        {
            ReadFilter.Nearest => ctx.NearestClamp,
            ReadFilter.Linear => ctx.LinearClamp,
            ReadFilter.Comparison => ctx.Comparison,
            _ => isDepth || format is GpuFormat.R32Float or GpuFormat.R32Uint ? ctx.NearestClamp : ctx.LinearClamp,
        };
    }

    private static bool IsBuffer(string name, in ResourceScope scope)
    {
        return ResourceRegistry.IsEngineBuffer(name) || (!ResourceRegistry.IsEngineTexture(name) && scope.TryBuffer(name, out _, out _));
    }

    private static uint Iterations(Pass pass, in FrameCounts counts)
    {
        return pass.Each.Over == EachOver.Bricks ? Math.Min(pass.Each.Max, counts.BrickJobs) : Math.Max(1, pass.Each.Max);
    }

    private static uint Ceil(uint count, uint per)
    {
        return (Math.Max(1, count) + per - 1) / per;
    }

    private static bool WritesName(Pass pass, string name)
    {
        foreach (PassWrite write in pass.Writes)
        {
            if (string.Equals(write.Name, name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool ReadsName(Pass pass, string name)
    {
        foreach (PassRead read in pass.Reads)
        {
            if (string.Equals(read.Name, name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The pass cannot run this frame: a name stands for nothing yet, because the pass that makes it is not ready (the
    /// listing's fit check has already said so when nothing makes it at all). Noted once, verbosely. Always false, so a
    /// caller returns it.
    /// </summary>
    private static bool Skip(RenderContext ctx, PassState pass, string name)
    {
        ctx.Pipeline.Note($"Pass {pass.Path} waits: {name} is not there to bind yet.");
        return false;
    }
}
