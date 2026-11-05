using Magic.Gpu;
using Magic.Rendering;

namespace GIRendering;

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
/// <see cref="GpuPipelines"/>. <see cref="Run(RenderGraph, in GpuConstants, string?, bool, GpuPipeline)"/> starts it, once a
/// frame or more.
/// </summary>
internal sealed class GpuPass
{
    public required string Name { get; init; }

    /// <summary>
    /// What a compute entry's dispatches are sized by, along x.
    /// </summary>
    public uint GroupSizeX { get; internal set; }

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

    /// <summary>
    /// Whether it is one of the full-screen passes, which all run in tiles of one size.
    /// </summary>
    internal bool IsTiled { get; init; }

    internal GpuPipeline Pipeline { get; set; }

    /// <summary>
    /// The pass specialised to the ray-query tracer; none on a GPU that traces no rays, or for a pass that doesn't trace.
    /// </summary>
    internal GpuPipeline RayQueryPipeline { get; set; }

    /// <summary>
    /// Starts it as a compute pass, under <paramref name="name"/> when one run differs from another, with
    /// <paramref name="pipeline"/> in place of its own when there is one.
    /// </summary>
    public GpuPassRun Run(
        RenderGraph graph,
        in GpuConstants header,
        string? name = null,
        bool usesRayQuery = false,
        GpuPipeline pipeline = default
    )
    {
        graph.BeginComputePass(name ?? Name);

        return new GpuPassRun(graph, header, Choose(usesRayQuery, pipeline));
    }

    /// <summary>
    /// Starts it as a draw pass into <paramref name="targets"/>.
    /// </summary>
    public GpuPassRun Run(RenderGraph graph, in GpuConstants header, in GpuRenderTargets targets, string? name = null)
    {
        graph.BeginDrawPass(name ?? Name, targets);

        return new GpuPassRun(graph, header, Pipeline);
    }

    private GpuPipeline Choose(bool usesRayQuery, GpuPipeline pipeline)
    {
        if (!pipeline.IsNone)
            return pipeline;

        return usesRayQuery ? RayQueryPipeline : Pipeline;
    }
}

/// <summary>
/// A pass being recorded: say what it reads and writes, with the barriers that take, then end it with what it runs.
/// </summary>
internal readonly ref struct GpuPassRun
{
    private readonly RenderGraph _graph;
    private readonly GpuConstants _header;
    private readonly GpuPipeline _pipeline;

    internal GpuPassRun(RenderGraph graph, in GpuConstants header, GpuPipeline pipeline)
    {
        _graph = graph;
        _header = header;
        _pipeline = pipeline;
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
        _graph.Recorder.Dispatch(groupsX, groupsY, groupsZ);
        _graph.EndPass();
    }

    public void DispatchIndirect(GpuBuffer arguments, ulong offset)
    {
        Bind();
        _graph.Recorder.DispatchIndirect(arguments, offset);
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
        _graph.Recorder.DrawIndirectCount(arguments, argumentsOffset, counts, countOffset, maxDraws, stride);
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
        _graph.Recorder.BindPipeline(_pipeline);
        _graph.Recorder.PushConstants(_header);
    }
}
