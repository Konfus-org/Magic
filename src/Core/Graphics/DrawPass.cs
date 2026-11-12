using System.Drawing;

namespace Magic.Graphics;

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
    /// As many draws as the unsigned count at <paramref name="countOffset"/> in <paramref name="count"/>, up to
    /// <paramref name="maxDraws"/>, their arguments <paramref name="stride"/> bytes apart from
    /// <paramref name="argumentsOffset"/> in <paramref name="arguments"/>, each four unsigned integers: vertices,
    /// instances, first vertex, first instance.
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
        _graph.Add(
            new()
            {
                Kind = GpuCommandKind.DrawIndirectCount,
                Id = arguments.Id,
                Offset = argumentsOffset,
                Id2 = count.Id,
                Offset2 = countOffset,
                X = maxDraws,
                Y = stride,
            }
        );
        _graph.EndPass();
    }

    /// <summary>
    /// Each of <paramref name="draws"/> in order, clipped to its scissor with its constants pushed, their 32-bit
    /// indices read from <paramref name="indices"/>.
    /// </summary>
    public void DrawIndexed(GpuBuffer indices, scoped ReadOnlySpan<IndexedDraw> draws)
    {
        _graph.Use(indices, GpuAccess.Index);
        _graph.Add(new() { Kind = GpuCommandKind.BindIndexBuffer, Id = indices.Id });
        foreach (IndexedDraw draw in draws)
        {
            // A draw with no pixels to draw into is left out, as a negative size would wrap round to a huge one.
            if (draw.Scissor.Width <= 0 || draw.Scissor.Height <= 0)
                continue;

            _graph.Add(
                new()
                {
                    Kind = GpuCommandKind.SetScissor,
                    X = (uint)draw.Scissor.X,
                    Y = (uint)draw.Scissor.Y,
                    Z = (uint)draw.Scissor.Width,
                    W = (uint)draw.Scissor.Height,
                }
            );
            _graph.PushConstants(draw.Constants);
            _graph.Add(
                new()
                {
                    Kind = GpuCommandKind.DrawIndexed,
                    X = draw.IndexCount,
                    Z = draw.FirstIndex,
                }
            );
        }

        _graph.EndPass();
    }

    private DrawPass Use(scoped ReadOnlySpan<GpuResource> resources, GpuAccess access)
    {
        _graph.Use(resources, access);

        return this;
    }
}
