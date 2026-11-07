using Magic.Gpu;

namespace Magic.Rendering;

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
/// GPU provider puts the barriers in for, then end it with what it dispatches.
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
        foreach (GpuResource resource in resources)
        {
            if (resource.IsTexture)
                _graph.Use(resource.Texture, access);
            else
                _graph.Use(resource.Buffer, access);
        }

        return this;
    }
}
