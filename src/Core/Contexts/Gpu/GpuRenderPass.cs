using Rectangle = System.Drawing.Rectangle;

namespace Magic.Contexts.Gpu;

/// <summary>
/// A render pass being recorded (<see cref="GpuCommands.RenderPass"/>): the commands legal inside one, each handing
/// the pass back so they chain. Disposing it ends the pass, so it is a <c>using</c> block. What is bound stays bound
/// until the pass ends; a draw uses whatever is bound when it is recorded.
/// </summary>
public readonly record struct GpuRenderPass : IDisposable
{
    private readonly GpuCommands _commands;

    internal GpuRenderPass(GpuCommands commands) => _commands = commands;

    public void Dispose() => _commands.EndRenderPass();

    /// <summary>
    /// Where the targets are drawn, in their pixels.
    /// </summary>
    public GpuRenderPass Viewport(Rectangle area)
    {
        _commands.Viewport(area);

        return this;
    }

    /// <summary>
    /// Outside which nothing is drawn, in the targets' pixels.
    /// </summary>
    public GpuRenderPass Scissor(Rectangle area)
    {
        _commands.Scissor(area);

        return this;
    }

    /// <summary>
    /// The graphics pipeline the draws after it use.
    /// </summary>
    public GpuRenderPass Pipeline(GpuPipeline pipeline)
    {
        _commands.Pipeline(pipeline);

        return this;
    }

    /// <summary>
    /// The index buffer, of indices <paramref name="size"/> wide.
    /// </summary>
    public GpuRenderPass IndexBuffer(GpuBuffer buffer, GpuIndexSize size)
    {
        _commands.IndexBuffer(buffer, size);

        return this;
    }

    /// <summary>
    /// The pipeline's one constant block, which both its stages see, copied now: at most 128 bytes, laid out as the
    /// shaders' <c>CONSTANTS</c> struct is (Shaders/Include/Heap.hlsli), for the draws after it.
    /// </summary>
    public GpuRenderPass Constants<T>(in T constants)
        where T : unmanaged
    {
        _commands.Constants(in constants);

        return this;
    }

    /// <summary>
    /// What the pass's shaders read through the heap: each is waited for and laid out to read before the pass begins,
    /// wherever in the pass this is recorded.
    /// </summary>
    public GpuRenderPass Reads(ReadOnlySpan<GpuResource> resources)
    {
        _commands.Reads(resources);

        return this;
    }

    /// <summary>
    /// What the pass's fragment shaders write through the heap, beside its targets: each is waited for and laid out to
    /// write before the pass begins.
    /// </summary>
    public GpuRenderPass Writes(ReadOnlySpan<GpuResource> resources)
    {
        _commands.Writes(resources);

        return this;
    }

    public GpuRenderPass Draw(uint vertices, uint instances = 1, uint firstVertex = 0, uint firstInstance = 0)
    {
        _commands.Draw(vertices, instances, firstVertex, firstInstance);

        return this;
    }

    public GpuRenderPass DrawIndexed(
        uint indices,
        uint firstIndex = 0,
        int vertexOffset = 0,
        uint instances = 1,
        uint firstInstance = 0)
    {
        _commands.DrawIndexed(indices, firstIndex, vertexOffset, instances, firstInstance);

        return this;
    }

    /// <summary>
    /// <paramref name="count"/> indexed draws whose arguments the GPU reads from <paramref name="arguments"/> at
    /// <paramref name="offset"/>, 20 bytes each: indices, instances, first index, vertex offset, first instance.
    /// </summary>
    public GpuRenderPass DrawIndexedFromBuffer(GpuBuffer arguments, uint offset, uint count)
    {
        _commands.DrawIndexedFromBuffer(arguments, offset, count);

        return this;
    }
}
