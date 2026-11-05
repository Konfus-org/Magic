using Magic.Utils;

namespace Magic.Gpu;

/// <summary>
/// The GPU, as a gem that provides it: resources made from descriptions into one bindless heap, pipelines built from
/// SPIR-V, and one <see cref="GpuCommands"/> a frame, recorded as data and submitted whole. A gem never sees the API
/// behind it. Main thread, but where a member says otherwise.
/// </summary>
public interface IGraphicsApi
{
    GpuFeatures Features { get; }

    GpuLimits Limits { get; }

    /// <summary>
    /// The window's image for this frame, to draw into and transition to <see cref="GpuAccess.Present"/> last;
    /// <see cref="GpuTexture.None"/> headless, or while the window is minimised. A new one each frame.
    /// </summary>
    GpuTexture WindowTarget { get; }

    TextureDescription GetDescription(GpuTexture texture);

    BufferDescription GetDescription(GpuBuffer buffer);

    Result<GpuBuffer> CreateBuffer(in BufferDescription description);

    Result<GpuTexture> CreateTexture(in TextureDescription description);

    Result<GpuSampler> CreateSampler(in SamplerDescription description);

    Result<GpuPipeline> CreatePipeline(in ComputePipelineDescription description);

    Result<GpuPipeline> CreatePipeline(in GraphicsPipelineDescription description);

    /// <summary>
    /// A bottom-level acceleration structure over triangles of the heap's buffers, built by
    /// <see cref="GpuCommandRecorder.BuildAcceleration"/>; only when <see cref="GpuFeatures.HasRayQuery"/>.
    /// </summary>
    Result<GpuAcceleration> CreateAcceleration(in BottomLevelDescription description);

    /// <summary>
    /// A top-level acceleration structure over instances of bottom-level ones, built from its instance buffer by
    /// <see cref="GpuCommandRecorder.BuildAcceleration"/>; only when <see cref="GpuFeatures.HasRayQuery"/>.
    /// </summary>
    Result<GpuAcceleration> CreateAcceleration(in TopLevelDescription description);

    /// <summary>
    /// The device address of a bottom-level acceleration structure: what an instance of it refers to it by.
    /// </summary>
    ulong GetAddress(GpuAcceleration acceleration);

    /// <summary>
    /// Frees a resource once no frame in flight uses it. The handle is gone at once.
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
    /// The bytes of a <see cref="BufferUsages.Readback"/> buffer once every frame submitted before this call has
    /// finished on the GPU. Any thread.
    /// </summary>
    Task<Result<ReadOnlyMemory<byte>>> ReadBackAsync(
        GpuBuffer buffer,
        long offset,
        int bytes,
        CancellationToken cancel
    );

    /// <summary>
    /// Runs the frame's commands, once a frame: waits for the frame two before, uploads, records, submits and shows
    /// the window's image. What was never submitted in a frame still shows the window's image as it was.
    /// </summary>
    void Submit(in GpuCommands commands);
}
