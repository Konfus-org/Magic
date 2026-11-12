namespace Magic.Graphics;

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
        _graph.Add(
            new()
            {
                Kind = GpuCommandKind.Dispatch,
                X = groupsX,
                Y = groupsY,
                Z = groupsZ,
            }
        );
        _graph.EndPass();
    }

    /// <summary>
    /// Dispatches a thread a pixel over <paramref name="width"/> by <paramref name="height"/>, in each of
    /// <paramref name="depth"/> slices, in as many groups of the pass's shader as cover them.
    /// </summary>
    public void DispatchOver(int width, int height, int depth = 1)
    {
        _graph.Add(
            new()
            {
                Kind = GpuCommandKind.DispatchThreads,
                X = (uint)width,
                Y = (uint)height,
                Z = (uint)depth,
            }
        );
        _graph.EndPass();
    }

    /// <summary>
    /// Dispatches three unsigned group counts read from <paramref name="arguments"/> at <paramref name="offset"/>.
    /// </summary>
    public void DispatchIndirect(GpuBuffer arguments, ulong offset)
    {
        _graph.Add(
            new()
            {
                Kind = GpuCommandKind.DispatchIndirect,
                Id = arguments.Id,
                Offset = offset,
            }
        );
        _graph.EndPass();
    }

    private ComputePass Use(scoped ReadOnlySpan<GpuResource> resources, GpuAccess access)
    {
        _graph.Use(resources, access);

        return this;
    }
}
