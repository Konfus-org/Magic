using Magic.Utils;

namespace Magic.Rendering;

/// <summary>
/// The GPU, as a gem that provides it: resources made from descriptions into one bindless heap, pipelines built from
/// SPIR-V, and one <see cref="GpuCommands"/> a frame, recorded as data and submitted whole. A gem never sees the API
/// behind it. Render thread, but where a member says otherwise.
/// </summary>
public interface IGraphicsApi
{
    GpuFeatures Features { get; }

    GpuLimits Limits { get; }

    /// <summary>
    /// The window's image for this frame, to draw into until <see cref="Present"/> shows it; <see cref="GpuTexture.None"/>
    /// headless, or while the window is minimised. A new one each frame.
    /// </summary>
    GpuTexture WindowTarget { get; }

    TextureDescription GetDescription(GpuTexture texture);

    BufferDescription GetDescription(GpuBuffer buffer);

    Result<GpuBuffer> CreateBuffer(in BufferDescription description);

    Result<GpuTexture> CreateTexture(in TextureDescription description);

    /// <summary>
    /// The texture under the description's name: made on the first ask, the same one while the name is asked for with
    /// the same description, so what it holds carries from frame to frame, and made again, the old one destroyed, when
    /// the description changes. <see cref="RenderGraph.Texture"/> asks for it.
    /// </summary>
    Result<GpuTexture> GetTexture(in TextureDescription description);

    /// <summary>
    /// The buffer under the description's name, kept as <see cref="GetTexture"/> keeps a texture.
    /// <see cref="RenderGraph.Buffer"/> asks for it.
    /// </summary>
    Result<GpuBuffer> GetBuffer(in BufferDescription description);

    /// <summary>
    /// The top-level acceleration structure under the description's name, kept as <see cref="GetTexture"/> keeps a
    /// texture; only when <see cref="GpuFeatures.HasRayQuery"/>. <see cref="RenderGraph.Acceleration"/> asks
    /// for it.
    /// </summary>
    Result<GpuAcceleration> GetAcceleration(in TopLevelDescription description);

    /// <summary>
    /// Whether <paramref name="shader"/> is built for this GPU: a compute entry into its pipeline, a vertex or pixel
    /// entry compiled. The first ask starts compiling it. <see cref="RenderGraph.GetState"/> asks for it.
    /// </summary>
    GpuState GetState(in ShaderEntry shader);

    /// <summary>
    /// A texture for this frame alone, made to <paramref name="description"/> or taken back from an earlier frame's,
    /// which goes back to the pool as <see cref="Present"/> ends the frame. It comes from the provider, not the graph,
    /// because a pass records its heap id as it records.
    /// </summary>
    Result<GpuTexture> CreateTransient(in TextureDescription description);

    Result<GpuSampler> CreateSampler(in SamplerDescription description);

    Result<GpuPipeline> CreatePipeline(in ComputePipelineDescription description);

    Result<GpuPipeline> CreatePipeline(in GraphicsPipelineDescription description);

    /// <summary>
    /// A bottom-level acceleration structure over triangles of the heap's buffers, built by
    /// <see cref="RenderGraph.BuildAcceleration"/>; only when <see cref="GpuFeatures.HasRayQuery"/>.
    /// </summary>
    Result<GpuAcceleration> CreateAcceleration(in BottomLevelDescription description);

    /// <summary>
    /// A top-level acceleration structure over instances of bottom-level ones, built from its instance buffer by
    /// <see cref="RenderGraph.BuildAcceleration"/>; only when <see cref="GpuFeatures.HasRayQuery"/>.
    /// </summary>
    Result<GpuAcceleration> CreateAcceleration(in TopLevelDescription description);

    /// <summary>
    /// The device address of a bottom-level acceleration structure: what an instance of it refers to it by.
    /// </summary>
    ulong GetAddress(GpuAcceleration acceleration);

    /// <summary>
    /// Frees a resource once no frame in flight uses it. The handle is gone at once. Destroying <c>None</c> does
    /// nothing.
    /// </summary>
    void Destroy(GpuBuffer buffer);

    void Destroy(GpuTexture texture);

    void Destroy(GpuSampler sampler);

    void Destroy(GpuPipeline pipeline);

    void Destroy(GpuAcceleration acceleration);

    /// <summary>
    /// Room for <paramref name="bytes"/> in this frame's staging ring, to fill and copy from in this frame's commands.
    /// </summary>
    GpuUpload BeginUpload(int bytes);

    /// <summary>
    /// The bytes of a <see cref="BufferUsages.Readback"/> buffer once the frame being recorded as it is called has
    /// finished on the GPU. Any thread.
    /// </summary>
    Task<Result<ReadOnlyMemory<byte>>> ReadBackAsync(
        GpuBuffer buffer,
        long offset,
        int bytes,
        CancellationToken cancel
    );

    /// <summary>
    /// Ends the frame, once a frame, after every gem has recorded into Core's graph: records the passes, each after
    /// the barriers its uses need, submits them, shows the window's image, takes back the frame's transients, and
    /// begins the next frame, waiting for the one two before and taking the window's next image. Core calls it; a gem
    /// never does.
    /// </summary>
    void Present(in GpuCommands commands);
}
