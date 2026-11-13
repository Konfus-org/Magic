using System.Drawing;

namespace Magic.Graphics;

/// <summary>
/// One indexed draw of a <see cref="DrawPass"/>: <see cref="IndexCount"/> indices from <see cref="FirstIndex"/>,
/// clipped to <see cref="Scissor"/>, with <see cref="Constants"/> pushed over the pass's.
/// </summary>
public readonly record struct IndexedDraw
{
    public required Rectangle Scissor { get; init; }

    public required GpuConstants Constants { get; init; }

    public required uint IndexCount { get; init; }

    public uint FirstIndex { get; init; }
}

/// <summary>
/// A draw pass that <see cref="RenderGraph.Draw"/> begins: declare what it reads, then end it with what it draws.
/// </summary>
/// <remarks>
/// The GPU provider puts in the barriers from these declarations.
/// </remarks>
public readonly ref struct DrawPass
{
    private readonly RenderGraph _graph;

    internal DrawPass(RenderGraph graph) => _graph = graph;

    public DrawPass Reads(params ReadOnlySpan<GpuResource> resources) => Use(resources, GpuAccess.ShaderRead);

    public DrawPass Indirect(params ReadOnlySpan<GpuResource> resources) => Use(resources, GpuAccess.Indirect);

    /// <summary>
    /// Draws as many times as the unsigned count at <paramref name="countOffset"/> in <paramref name="count"/>, up to
    /// <paramref name="maxDraws"/>.
    /// </summary>
    /// <remarks>
    /// The arguments start at <paramref name="argumentsOffset"/> in <paramref name="arguments"/>,
    /// <paramref name="stride"/> bytes apart. Each is four unsigned integers: vertices, instances, first vertex, first
    /// instance.
    /// </remarks>
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
    /// Draws each of <paramref name="draws"/> in order with 32-bit indices from <paramref name="indices"/>, each
    /// clipped to its scissor with its constants pushed.
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
