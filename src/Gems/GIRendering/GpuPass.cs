using Magic.Gpu;
using Magic.Rendering;

namespace Magic.GIRendering;

/// <summary>
/// A buffer or a texture, as a pass names what it reads or writes.
/// </summary>
internal readonly record struct GpuResource(GpuBuffer Buffer, GpuTexture Texture, bool IsTexture)
{
    public static implicit operator GpuResource(GpuBuffer buffer) => new(buffer, default, false);

    public static implicit operator GpuResource(GpuTexture texture) => new(default, texture, true);
}

/// <summary>
/// One pass of the frame: its name and its pipeline, declared by the area that runs it and built by
/// <see cref="GpuPipelines"/>. <c>Run</c> starts it, once a
/// frame or more.
/// </summary>
internal sealed class GpuPass
{
    public required string Name { get; init; }

    /// <summary>
    /// A compute pass's threads per group; a draw pass has none.
    /// </summary>
    public uint GroupSizeX { get; internal set; }

    public uint GroupSizeY { get; internal set; }

    internal required string Module { get; init; }

    /// <summary>
    /// The compute or pixel entry.
    /// </summary>
    internal required string Entry { get; init; }

    internal required PassShaders Shaders { get; init; }

    /// <summary>
    /// The vertex entry; none for a compute pass.
    /// </summary>
    internal string? VertexEntry { get; init; }

    /// <summary>
    /// A draw pass's fixed state, over the description its shaders start.
    /// </summary>
    internal Func<GraphicsPipelineDescription, GraphicsPipelineDescription>? DrawState { get; init; }

    internal GpuPipeline Pipeline { get; set; }

    /// <summary>
    /// Starts it as a compute pass, under <paramref name="name"/> when one run differs from another.
    /// </summary>
    public GpuPassRun Run(RenderGraph graph, in GpuConstants header, string? name = null)
    {
        graph.BeginComputePass(name ?? Name);

        return new GpuPassRun(graph, this, header);
    }

    /// <summary>
    /// How many groups cover <paramref name="width"/> by <paramref name="height"/>, a thread a pixel.
    /// </summary>
    public (uint X, uint Y) GetGroups(int width, int height) =>
        ((uint)((width + GroupSizeX - 1) / GroupSizeX), (uint)((height + GroupSizeY - 1) / GroupSizeY));

    /// <summary>
    /// Starts it as a draw pass into <paramref name="targets"/>.
    /// </summary>
    public GpuPassRun Run(RenderGraph graph, in GpuConstants header, in GpuRenderTargets targets, string? name = null)
    {
        graph.BeginDrawPass(name ?? Name, targets);

        return new GpuPassRun(graph, this, header);
    }
}

/// <summary>
/// A pass being recorded: say what it reads and writes, with the barriers that take, then end it with what it runs.
/// </summary>
internal readonly ref struct GpuPassRun
{
    private readonly RenderGraph _graph;
    private readonly GpuPass _pass;
    private readonly GpuConstants _header;

    internal GpuPassRun(RenderGraph graph, GpuPass pass, in GpuConstants header)
    {
        _pass = pass;
        _graph = graph;
        _header = header;
    }

    public GpuPassRun Reads(params ReadOnlySpan<GpuResource> resources) => Use(resources, GpuAccess.ShaderRead);

    public GpuPassRun Reads(GpuTexture texture, int level)
    {
        _graph.Use(texture, GpuAccess.ShaderRead, level);

        return this;
    }

    public GpuPassRun Writes(params ReadOnlySpan<GpuResource> resources) => Use(resources, GpuAccess.ShaderWrite);

    public GpuPassRun Writes(GpuTexture texture, int level)
    {
        _graph.Use(texture, GpuAccess.ShaderWrite, level);

        return this;
    }

    public GpuPassRun Indirect(params ReadOnlySpan<GpuResource> resources) => Use(resources, GpuAccess.Indirect);

    public void Dispatch(uint groupsX, uint groupsY = 1, uint groupsZ = 1)
    {
        Bind();
        _graph.Dispatch(groupsX, groupsY, groupsZ);
        _graph.EndPass();
    }

    /// <summary>
    /// Dispatches a thread a pixel over <paramref name="width"/> by <paramref name="height"/>.
    /// </summary>
    public void DispatchOver(int width, int height)
    {
        (uint groupsX, uint groupsY) = _pass.GetGroups(width, height);
        Dispatch(groupsX, groupsY);
    }

    public void DispatchIndirect(GpuBuffer arguments, ulong offset)
    {
        Bind();
        _graph.DispatchIndirect(arguments, offset);
        _graph.EndPass();
    }

    public void DrawIndirectCount(
        GpuBuffer arguments,
        ulong argumentsOffset,
        GpuBuffer counts,
        ulong countOffset,
        uint maxDraws,
        uint stride
    )
    {
        Bind();
        _graph.DrawIndirectCount(arguments, argumentsOffset, counts, countOffset, maxDraws, stride);
        _graph.EndPass();
    }

    private GpuPassRun Use(scoped ReadOnlySpan<GpuResource> resources, GpuAccess access)
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

    private void Bind()
    {
        _graph.BindPipeline(_pass.Pipeline);
        _graph.PushConstants(_header);
    }
}
