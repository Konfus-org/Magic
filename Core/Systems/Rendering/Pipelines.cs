using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Rendering;
using Magic.Utils;

namespace Magic.Systems.Rendering;

/// <summary>
/// The material pipelines of <see cref="PipelineTable"/>. A class starts its compile on a worker the first time an
/// instance needs it (<see cref="Ensure"/>); until it lands the class answers 0 and its draws are skipped. A recompile (a surface, template or include
/// changed) keeps the last good pipeline until the new one is ready, and keeps it on failure too, with the compiler's
/// message logged. A class that never had a good pipeline answers with the forced failure pipeline instead, so a broken
/// shader shows up magenta rather than as a hole in the scene. A change made while a compile runs starts another, which
/// supersedes it.
/// </summary>
internal static class Pipelines
{
    public const string Template = "Templates/Forward.frag.hlsl";
    public const string VertexTemplate = "Templates/Mesh.vert.hlsl";
    private const string Contract = "Include/Surface.hlsli";

    // The fixed 48-byte vertex in slot 0 plus the instance slot from an instance-rate buffer in slot 1: the index is never
    // taken from SV_InstanceID, whose relation to first_instance differs between backends.
    private static readonly VertexBufferLayout[] VertexBuffers = [new(0, Vertex.Size), new(1, 4, PerInstance: true)];

    private static readonly VertexAttribute[] VertexAttributes =
    [
        new(0, 0, GpuVertexFormat.Float3, 0),
        new(1, 0, GpuVertexFormat.Float3, 12),
        new(2, 0, GpuVertexFormat.Float4, 24),
        new(3, 0, GpuVertexFormat.Float2, 40),
        new(4, 1, GpuVertexFormat.Uint, 0),
    ];

    /// <summary>Starts the class's first compile, unless it has one.</summary>
    public static void Ensure(RenderContext ctx, PipelineClass cls)
    {
        if (ctx.Pipelines.Built.ContainsKey(cls))
            return;

        ctx.Pipelines.Built[cls] = default;
        Start(ctx, cls);
    }

    /// <summary>The pipeline for the class: 0 while its first compile runs, the forced failure pipeline once that compile failed. Reads only.</summary>
    public static GpuPipeline Get(RenderContext ctx, PipelineClass cls)
    {
        PipelineTable table = ctx.Pipelines;
        BuiltPipeline built = table.Built.GetValueOrDefault(cls);
        if (!built.Pipeline.IsValid && built.Error is not null && cls != table.Forced && table.Forced.Surface != 0)
            return table.Built.GetValueOrDefault(table.Forced).Pipeline;

        return built.Pipeline;
    }

    /// <summary>Compiles the class now, on this thread, so the first frame can draw it.</summary>
    public static void Prewarm(RenderContext ctx, PipelineClass cls)
    {
        Ensure(ctx, cls);
        ctx.Pipelines.Compiles.Wait(cls);
        Update(ctx);
    }

    /// <summary>Main thread, once per frame: turns finished compiles into pipelines.</summary>
    public static void Update(RenderContext ctx)
    {
        ctx.Pipelines.Compiles.Poll(ctx, Finish);
    }

    /// <summary>
    /// Shaders changed (with everything that includes them): recompiles every class built on them. A change under Include/
    /// or Templates/ is part of every class's source, so all of them recompile, the vertex shader first.
    /// </summary>
    public static void Invalidate(RenderContext ctx, IReadOnlySet<ulong> shaderIds)
    {
        bool everything = false;
        foreach (ulong id in shaderIds)
        {
            string path = ctx.Assets.PathOf(id) ?? "";
            everything |= path.Contains("/Include/", StringComparison.OrdinalIgnoreCase) || path.Contains("/Templates/", StringComparison.OrdinalIgnoreCase);
        }

        if (everything && Shaders.GetByPath(ctx, VertexTemplate) is { } vertex)
        {
            // The classes restart once the new vertex shader is in (Finish).
            ctx.Pipelines.Compiles.Start(PipelineTable.VertexKey, Shaders.CompileAsync(ctx, vertex.Text, vertex.Path, GpuStage.Vertex, Shaders.ClosureHash(ctx, vertex)));
            return;
        }

        foreach (PipelineClass cls in ctx.Pipelines.Built.Keys.ToArray())
        {
            if (shaderIds.Contains(cls.Surface))
                Start(ctx, cls);
        }
    }

    private static void Start(RenderContext ctx, PipelineClass cls)
    {
        SurfaceSource? surface = Shaders.Surface(ctx, cls.Surface);
        Shader? surfaceShader = Shaders.Get(ctx, new Handle<Shader>(cls.Surface));
        Shader? template = Shaders.GetByPath(ctx, Template);
        Shader? contract = Shaders.GetByPath(ctx, Contract);
        if (surface is null || surfaceShader is null || template is null || contract is null)
        {
            ctx.Pipelines.Built[cls] = ctx.Pipelines.Built[cls] with { Error = "missing surface, template or Include/Surface.hlsli" };
            return;
        }

        string composed = SurfaceComposer.Compose(surface, cls.Variant, template.Path, template.Text);
        // The contract is included by the composed text, not by any asset, so its closure goes into the salt by hand;
        // otherwise editing it would serve stale bytecode from the cache.
        string salt = Shaders.ClosureHash(ctx, template) + Shaders.ClosureHash(ctx, surfaceShader) + contract.Text + Shaders.ClosureHash(ctx, contract);
        ctx.Pipelines.Compiles.Start(cls, Shaders.CompileAsync(ctx, composed, $"{surface.Path}+{template.Path}:{cls.Variant}", GpuStage.Fragment, salt));
    }

    /// <summary>A compile finished: the vertex shader is swapped, or the class gets its pipeline (or keeps its last one and the error).</summary>
    private static void Finish(RenderContext ctx, PipelineClass cls, Result<CompiledShader> result)
    {
        PipelineTable table = ctx.Pipelines;
        if (cls == PipelineTable.VertexKey)
        {
            if (result.Failed)
                Debugging.Log.Error($"Mesh vertex shader: {result.Message} (keeping the last good one)");
            else
                table.VertexShader = result.Payload;

            // A shared file changed: every class builds again, linked with the current vertex shader (their fragment
            // bytecode comes from the disk cache unless it changed too).
            foreach (PipelineClass rebuilt in table.Built.Keys.ToArray())
                Start(ctx, rebuilt);
            return;
        }

        BuiltPipeline built = table.Built.GetValueOrDefault(cls);
        if (result.Failed)
        {
            if (built.Error != result.Message)
                Debugging.Log.Error($"Pipeline {Describe(ctx, cls)}: {result.Message}{(built.Pipeline.IsValid ? " (keeping the last good pipeline)" : " (drawing the failure pipeline)")}");
            table.Built[cls] = built with { Error = result.Message };
            return;
        }

        try
        {
            PipelineDesc desc = new(table.VertexShader, result.Payload, table.ColorFormat)
            {
                Buffers = VertexBuffers,
                Attributes = VertexAttributes,
                Depth = table.DepthFormat,
                Cull = cls.Variant.HasFlag(SurfaceVariant.DoubleSided) ? GpuCull.None : GpuCull.Back,
            };
            GpuPipeline pipeline = ctx.Gpu.CreatePipeline(desc);
            ctx.Gpu.Release(built.Pipeline);
            table.Built[cls] = new BuiltPipeline(pipeline, null);
            Debugging.Log.Debug($"Pipeline ready: {Describe(ctx, cls)}.");
        }
        catch (InvalidOperationException ex)
        {
            table.Built[cls] = built with { Error = ex.Message };
            Debugging.Log.Error($"Pipeline {Describe(ctx, cls)}: {ex.Message}");
        }
    }

    private static string Describe(RenderContext ctx, PipelineClass cls)
    {
        return $"{Shaders.Surface(ctx, cls.Surface)?.Path ?? cls.Surface.ToString()} {cls.Variant}";
    }
}
