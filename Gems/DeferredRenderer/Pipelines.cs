using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Rendering;
using Magic.Utils;
using System.Text;

namespace DeferredRendererGem;

/// <summary>
/// The material pipelines of <see cref="PipelineTable"/>. A class starts its compile on a worker the first time an
/// instance needs it (<see cref="Acquire"/>); until it lands the class answers 0 and its draws are skipped. When its last
/// instance goes its pipeline is released (<see cref="Release"/>). A recompile (a surface, template or include
/// changed) keeps the last good pipeline until the new one is ready, and keeps it on failure too, with the compiler's
/// message logged. A class that never had a good pipeline answers with the forced failure pipeline instead, so a broken
/// shader shows up magenta rather than as a hole in the scene. A change made while a compile runs starts another, which
/// supersedes it.
/// </summary>
internal static class Pipelines
{
    public const string Template = "Templates/GBuffer.frag.hlsl";
    public const string VertexTemplate = "Templates/Mesh.vert.hlsl";
    private const string Contract = "Include/Surface.hlsli";

    // The fixed 48-byte vertex in slot 0 plus the instance slot and its LOD fade (GpuVisible) from an instance-rate buffer
    // in slot 1: the index is never taken from SV_InstanceID, whose relation to first_instance differs between backends.
    private static readonly VertexBufferLayout[] VertexBuffers = [new(0, Vertex.Size), new(1, GpuVisible.Size, PerInstance: true)];

    private static readonly VertexAttribute[] VertexAttributes =
    [
        new(0, 0, GpuVertexFormat.Float3, 0),
        new(1, 0, GpuVertexFormat.Float3, 12),
        new(2, 0, GpuVertexFormat.Float4, 24),
        new(3, 0, GpuVertexFormat.Float2, 40),
        new(4, 1, GpuVertexFormat.Uint, 0),
        new(5, 1, GpuVertexFormat.Float, 4),
    ];

    /// <summary>
    /// Takes a reference to the class, starting its first compile when it is new.
    /// </summary>
    public static void Acquire(RenderContext ctx, PipelineClass cls)
    {
        if (ctx.Pipelines.TryAcquire(cls, out _))
            return;

        ctx.Pipelines.Add(cls, default);
        StartCompile(ctx, cls);
    }

    /// <summary>
    /// Drops a reference to the class; the last one releases its pipeline, and its surface's text when no other class
    /// is built on it. A compile still running for it is ignored when it lands.
    /// </summary>
    public static void Release(RenderContext ctx, PipelineClass cls)
    {
        PipelineTable table = ctx.Pipelines;
        if (!table.Release(cls, out BuiltPipeline built))
            return;

        Debugging.Log.Verbose($"Pipeline released: {Describe(ctx, cls)}.");
        ctx.Gpu.Release(built.Pipeline);
        if (!table.Entries.Any(entry => entry.Key.Surface == cls.Surface))
            ctx.Shaders.Entries.Remove(cls.Surface);
    }

    /// <summary>
    /// The pipeline for the class: 0 while its first compile runs, the forced failure pipeline once that compile failed. Reads only.
    /// </summary>
    public static GpuPipeline Get(RenderContext ctx, PipelineClass cls)
    {
        PipelineTable table = ctx.Pipelines;
        table.TryGet(cls, out BuiltPipeline built);
        if (!built.Pipeline.IsValid && built.Error is not null && cls != table.Forced && table.Forced.Surface != 0)
            table.TryGet(table.Forced, out built);

        return built.Pipeline;
    }

    /// <summary>
    /// Compiles the class now, on this thread, so the first frame can draw it. The reference it takes is kept for good.
    /// </summary>
    public static void Prewarm(RenderContext ctx, PipelineClass cls)
    {
        Acquire(ctx, cls);
        ctx.Pipelines.Compiles.Wait(cls);
        ctx.Pipelines.Compiles.Poll(ctx, FinishCompile);
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
            // The classes restart once the new vertex shader is in (FinishCompile).
            ctx.Pipelines.Compiles.Start(PipelineTable.VertexKey, cancel => Shaders.CompileAsync(ctx, vertex.Text, vertex.Path, GpuStage.Vertex, Shaders.ClosureHash(ctx, vertex), cancel));
            return;
        }

        foreach ((PipelineClass cls, _) in ctx.Pipelines.Entries.ToArray())
        {
            if (shaderIds.Contains(cls.Surface))
                StartCompile(ctx, cls);
        }
    }

    /// <summary>
    /// A compile finished: the vertex shader is swapped, or the class gets its pipeline (or keeps its last one and the
    /// error). Main thread: the render system polls the table's compiles with it once per frame.
    /// </summary>
    public static void FinishCompile(RenderContext ctx, PipelineClass cls, Task<Result<CompiledShader>> job)
    {
        Result<CompiledShader> result = Shaders.Outcome(job);
        PipelineTable table = ctx.Pipelines;
        if (cls == PipelineTable.VertexKey)
        {
            if (result.Failed)
                Debugging.Log.Error($"Mesh vertex shader: {result.Message} (keeping the last good one)");
            else
                table.VertexShader = result.Payload;

            // A shared file changed: every class builds again, linked with the current vertex shader (their fragment
            // bytecode comes from the disk cache unless it changed too).
            foreach ((PipelineClass rebuilt, _) in table.Entries.ToArray())
                StartCompile(ctx, rebuilt);
            return;
        }

        if (!table.TryGet(cls, out BuiltPipeline built))
            return; // released while compiling

        if (result.Failed)
        {
            if (built.Error != result.Message)
                Debugging.Log.Error($"Pipeline {Describe(ctx, cls)}: {result.Message}{(built.Pipeline.IsValid ? " (keeping the last good pipeline)" : " (drawing the failure pipeline)")}");
            table.Set(cls, built with { Error = result.Message });
            return;
        }

        try
        {
            PipelineDesc desc = new(table.VertexShader, result.Payload, table.ColorFormats)
            {
                Buffers = VertexBuffers,
                Attributes = VertexAttributes,
                Depth = table.DepthFormat,
                Cull = cls.Variant.HasFlag(SurfaceVariant.DoubleSided) ? GpuCull.None : GpuCull.Back,
            };
            GpuPipeline pipeline = ctx.Gpu.CreatePipeline(desc);
            ctx.Gpu.Release(built.Pipeline);
            table.Set(cls, new BuiltPipeline(pipeline, null));
            Debugging.Log.Verbose($"Pipeline ready: {Describe(ctx, cls)}.");
        }
        catch (InvalidOperationException ex)
        {
            table.Set(cls, built with { Error = ex.Message });
            Debugging.Log.Error($"Pipeline {Describe(ctx, cls)}: {ex.Message}");
        }
    }

    private static void StartCompile(RenderContext ctx, PipelineClass cls)
    {
        SurfaceSource? surface = Shaders.Surface(ctx, cls.Surface);
        Shader? surfaceShader = Shaders.Get(ctx, new Handle<Shader>(cls.Surface));
        Shader? template = Shaders.GetByPath(ctx, Template);
        Shader? contract = Shaders.GetByPath(ctx, Contract);
        if (surface is null || surfaceShader is null || template is null || contract is null)
        {
            ctx.Pipelines.TryGet(cls, out BuiltPipeline built);
            ctx.Pipelines.Set(cls, built with { Error = "missing surface, template or Include/Surface.hlsli" });
            return;
        }

        string composed = Compose(surface, cls.Variant, template.Path, template.Text);
        // The contract is included by the composed text, not by any asset, so its closure goes into the salt by hand;
        // otherwise editing it would serve stale bytecode from the cache.
        string salt = Shaders.ClosureHash(ctx, template) + Shaders.ClosureHash(ctx, surfaceShader) + contract.Text + Shaders.ClosureHash(ctx, contract);
        ctx.Pipelines.Compiles.Start(cls, cancel => Shaders.CompileAsync(ctx, composed, $"{surface.Path}+{template.Path}:{cls.Variant}", GpuStage.Fragment, salt, cancel));
    }

    /// <summary>
    /// The HLSL of one class's fragment stage: the variant defines, the surface contract, the surface shader (stripped of
    /// what DXC must not see), the generated parameter loader, then the template. <c>#line</c> directives keep DXC's
    /// messages pointing at the real files.
    /// </summary>
    private static string Compose(SurfaceSource surface, SurfaceVariant variant, string templatePath, string templateText)
    {
        StringBuilder sb = new(surface.Stripped.Length + templateText.Length + 512);
        sb.Append("#define SURFACE_MASKED ").Append(variant.HasFlag(SurfaceVariant.Masked) ? '1' : '0').Append('\n');
        sb.Append("#define SURFACE_DOUBLE_SIDED ").Append(variant.HasFlag(SurfaceVariant.DoubleSided) ? '1' : '0').Append('\n');
        if (variant.HasFlag(SurfaceVariant.FailureForced))
            sb.Append("#define FAILURE_FORCE 1\n");
        sb.Append("#include \"Include/Surface.hlsli\"\n");
        sb.Append("#line 1 \"").Append(surface.Path).Append("\"\n");
        sb.Append(surface.Stripped);
        if (!surface.Stripped.EndsWith('\n'))
            sb.Append('\n');
        sb.Append("#line 1 \"").Append(surface.Path).Append(".loader\"\n");
        sb.Append(surface.Layout.EmitLoader("LoadMaterialParams", "Materials"));
        sb.Append("#line 1 \"").Append(templatePath).Append("\"\n");
        sb.Append(templateText);
        return sb.ToString();
    }

    private static string Describe(RenderContext ctx, PipelineClass cls)
    {
        return $"{Shaders.Surface(ctx, cls.Surface)?.Path ?? cls.Surface.ToString()} {cls.Variant}";
    }
}
