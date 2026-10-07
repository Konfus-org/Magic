using Magic.Contexts.Rendering;
using Magic.Utils;

namespace DeferredRendererGem;

/// <summary>
/// A surface shader and the variant of it a material needs: what the indirect draw list buckets by.
/// </summary>
internal readonly record struct PipelineClass(ulong Surface, SurfaceVariant Variant);

/// <summary>
/// What a class has now: its pipeline (0 until the first good compile) and its last error.
/// </summary>
internal readonly record struct BuiltPipeline(GpuPipeline Pipeline, string? Error);

/// <summary>
/// Every material pipeline the frame may need, class to <see cref="BuiltPipeline"/>, reference counted by the instances
/// drawn with the class, all built on the one gbuffer template and the one mesh vertex shader, with the compiles still
/// running. <see cref="Forced"/> is the class drawn in place of
/// one whose surface never compiled: the failure surface, forced to solid magenta.
/// </summary>
internal sealed class PipelineTable(CompiledShader vertexShader, PipelineClass forced, GpuFormat[] colorFormats, GpuFormat depthFormat) : RefCountTable<PipelineClass, BuiltPipeline>
{
    public CompiledShader VertexShader { get; set; } = vertexShader;

    public PipelineClass Forced { get; } = forced;

    public GpuFormat[] ColorFormats { get; } = colorFormats;

    public GpuFormat DepthFormat { get; } = depthFormat;

    public Pending<PipelineClass, Result<CompiledShader>> Compiles { get; } = new();

    /// <summary>
    /// The mesh vertex shader compiling again because a file every class shares changed; its one key is 0.
    /// </summary>
    public Pending<byte, Result<CompiledShader>> VertexCompile { get; } = new();

    public int Pending => Compiles.InFlight + VertexCompile.InFlight;
}
