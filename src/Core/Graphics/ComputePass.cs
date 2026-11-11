using System.Drawing;

namespace Magic.Graphics;

/// <summary>
/// A buffer or a texture, as a pass names what it reads or writes.
/// </summary>
public readonly record struct GpuResource(GpuBuffer Buffer, GpuTexture Texture, bool IsTexture)
{
    public static implicit operator GpuResource(GpuBuffer buffer) => new(buffer, default, false);

    public static implicit operator GpuResource(GpuTexture texture) => new(default, texture, true);
}

/// <summary>
/// A compute pass being recorded, begun by <see cref="RenderGraph.Compute"/>: say what it reads and writes, which the
/// GPU provider puts the barriers in for, a <c>None</c> one left out, then end it with what it dispatches.
/// </summary>
public readonly ref struct ComputePass
{
    private readonly RenderGraph _graph;

    internal ComputePass(RenderGraph graph) => _graph = graph;

    public ComputePass Reads(params ReadOnlySpan<GpuResource> resources) => Use(resources, GpuAccess.ShaderRead);

    public ComputePass Reads(GpuTexture texture, int level)
    {
        _graph.Use(texture, GpuAccess.ShaderRead, level);

        return this;
    }

    public ComputePass Writes(params ReadOnlySpan<GpuResource> resources) => Use(resources, GpuAccess.ShaderWrite);

    public ComputePass Writes(GpuTexture texture, int level)
    {
        _graph.Use(texture, GpuAccess.ShaderWrite, level);

        return this;
    }

    public ComputePass Indirect(params ReadOnlySpan<GpuResource> resources) => Use(resources, GpuAccess.Indirect);

    public void Dispatch(uint groupsX, uint groupsY = 1, uint groupsZ = 1)
    {
        _graph.Dispatch(groupsX, groupsY, groupsZ);
        _graph.EndPass();
    }

    /// <summary>
    /// Dispatches a thread a pixel over <paramref name="width"/> by <paramref name="height"/>.
    /// </summary>
    public void DispatchOver(int width, int height)
    {
        _graph.DispatchThreads((uint)width, (uint)height, 1);
        _graph.EndPass();
    }

    public void DispatchIndirect(GpuBuffer arguments, ulong offset)
    {
        _graph.DispatchIndirect(arguments, offset);
        _graph.EndPass();
    }

    private ComputePass Use(scoped ReadOnlySpan<GpuResource> resources, GpuAccess access)
    {
        _graph.Use(resources, access);

        return this;
    }
}

/// <summary>
/// One indexed draw of a <see cref="DrawPass"/>: <see cref="IndexCount"/> indices from <see cref="FirstIndex"/> on,
/// drawn only inside <see cref="Scissor"/>'s pixels of the target, with <see cref="Constants"/> pushed over the pass's.
/// </summary>
public readonly record struct IndexedDraw
{
    public required Rectangle Scissor { get; init; }

    public required GpuConstants Constants { get; init; }

    public required uint IndexCount { get; init; }

    public uint FirstIndex { get; init; }
}

/// <summary>
/// A draw pass being recorded, begun by <see cref="RenderGraph.Draw"/>: say what it reads, which the GPU provider puts
/// the barriers in for, then end it with what it draws.
/// </summary>
public readonly ref struct DrawPass
{
    private readonly RenderGraph _graph;

    internal DrawPass(RenderGraph graph) => _graph = graph;

    public DrawPass Reads(params ReadOnlySpan<GpuResource> resources) => Use(resources, GpuAccess.ShaderRead);

    public DrawPass Indirect(params ReadOnlySpan<GpuResource> resources) => Use(resources, GpuAccess.Indirect);

    /// <summary>
    /// As <see cref="RenderGraph.DrawIndirectCount"/> draws.
    /// </summary>
    public void DrawIndirectCount(
        GpuBuffer arguments,
        ulong argumentsOffset,
        GpuBuffer count,
        ulong countOffset,
        uint maxDraws,
        uint stride = 16
    )
    {
        _graph.DrawIndirectCount(arguments, argumentsOffset, count, countOffset, maxDraws, stride);
        _graph.EndPass();
    }

    /// <summary>
    /// Each of <paramref name="draws"/> in order, their 32-bit indices read from <paramref name="indices"/>.
    /// </summary>
    public void DrawIndexed(GpuBuffer indices, scoped ReadOnlySpan<IndexedDraw> draws)
    {
        _graph.DrawIndexed(indices, draws);
        _graph.EndPass();
    }

    private DrawPass Use(scoped ReadOnlySpan<GpuResource> resources, GpuAccess access)
    {
        _graph.Use(resources, access);

        return this;
    }
}
