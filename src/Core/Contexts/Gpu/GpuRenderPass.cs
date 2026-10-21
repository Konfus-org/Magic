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
    /// The vertex buffers, from slot 0, as the pipeline's <see cref="GpuPipelineDescription.VertexBuffers"/> number
    /// them.
    /// </summary>
    public GpuRenderPass VertexBuffers(ReadOnlySpan<GpuBuffer> buffers)
    {
        _commands.VertexBuffers(buffers);
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
    /// The buffers <paramref name="stage"/> (vertex or fragment) reads, from register 0, as its <c>READ(n)</c>s number
    /// them after its textures (Shaders/Include/Bindings.hlsli).
    /// </summary>
    public GpuRenderPass Buffers(GpuStage stage, ReadOnlySpan<GpuBuffer> buffers)
    {
        _commands.Buffers(stage, buffers);
        return this;
    }

    /// <summary>
    /// The textures <paramref name="stage"/> reads texel by texel, with no sampler, from register 0, as its
    /// <c>READ(n)</c>s number them after its sampled textures.
    /// </summary>
    public GpuRenderPass RawTextures(GpuStage stage, ReadOnlySpan<GpuTexture> textures)
    {
        _commands.RawTextures(stage, textures);
        return this;
    }

    /// <summary>
    /// The textures <paramref name="stage"/> reads through a sampler, from register 0: the nth is its <c>READ(n)</c>
    /// with <c>SAMPLER(n)</c>.
    /// </summary>
    public GpuRenderPass Textures(GpuStage stage, ReadOnlySpan<GpuSampledTexture> textures)
    {
        _commands.Textures(stage, textures);
        return this;
    }

    /// <summary>
    /// The one constant block of <paramref name="stage"/> (<c>UNIFORM(0)</c>), copied now: a struct laid out as the
    /// <c>cbuffer</c> is, in whole 16-byte rows.
    /// </summary>
    public GpuRenderPass Constants<T>(GpuStage stage, in T constants)
        where T : unmanaged
    {
        _commands.Constants(stage, in constants);
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
