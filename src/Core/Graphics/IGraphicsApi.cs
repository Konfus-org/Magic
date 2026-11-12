using Magic.Utils;

namespace Magic.Graphics;

/// <summary>
/// The GPU, as a gem that provides it: resources made by name from descriptions into one bindless heap, and one
/// <see cref="GpuCommands"/> a frame, recorded as data and submitted whole. Gems record their work and ask for their
/// resources on Core's <see cref="RenderGraph"/>, which asks the provider for them, and Core presents each frame; a gem
/// calls the provider itself only for what it has and for what a frame hands back. Render thread, but where a member
/// says otherwise.
/// </summary>
public interface IGraphicsApi
{
    GpuFeatures Features { get; }

    /// <summary>
    /// The window's image for this frame, to draw into until <see cref="Present"/> shows it; <see cref="GpuTexture.None"/>
    /// headless, or while the window is minimised. A new one each frame. <see cref="RenderGraph.WindowTarget"/> asks for
    /// it.
    /// </summary>
    GpuTexture WindowTarget { get; }

    TextureDescription GetDescription(GpuTexture texture);

    /// <summary>
    /// The texture under the description's name: made on the first ask, the same one while the name is asked for with
    /// the same description, so what it holds carries from frame to frame, made again, the old one destroyed, when the
    /// description changes, and destroyed once no frame has asked for it in a few. <see cref="RenderGraph.Texture"/>
    /// asks for it.
    /// </summary>
    Result<GpuTexture> GetTexture(in TextureDescription description);

    /// <summary>
    /// The buffer under the description's name, kept as <see cref="GetTexture"/> keeps a texture, except that one asked
    /// for at another size alone keeps what it holds, as much as fits, copied into the new one before the frame's
    /// passes.
    /// <see cref="RenderGraph.Buffer"/> asks for it.
    /// </summary>
    Result<GpuBuffer> GetBuffer(in BufferDescription description);

    /// <summary>
    /// The bottom-level acceleration structure under the description's name, its device address on the handle, kept as
    /// <see cref="GetTexture"/> keeps a texture; only when <see cref="GpuFeatures.HasRayQuery"/>.
    /// <see cref="RenderGraph.Acceleration(in BottomLevelDescription)"/> asks for it.
    /// </summary>
    Result<GpuAcceleration> GetAcceleration(in BottomLevelDescription description);

    /// <summary>
    /// The top-level acceleration structure under the description's name, kept as <see cref="GetTexture"/> keeps a
    /// texture; only when <see cref="GpuFeatures.HasRayQuery"/>.
    /// <see cref="RenderGraph.Acceleration(in TopLevelDescription)"/> asks for it.
    /// </summary>
    Result<GpuAcceleration> GetAcceleration(in TopLevelDescription description);

    /// <summary>
    /// The sampler <paramref name="description"/> makes, one for every ask with the same description, kept as
    /// <see cref="GetTexture"/> keeps a texture. <see cref="RenderGraph.Sampler"/> asks for it.
    /// </summary>
    Result<GpuSampler> GetSampler(in SamplerDescription description);

    /// <summary>
    /// Whether <paramref name="shader"/> is built for this GPU: a compute entry into its pipeline, a vertex or pixel
    /// entry compiled. The first ask starts compiling it.
    /// <see cref="RenderGraph.GetState(in ShaderEntry)"/> asks for it.
    /// </summary>
    GpuState GetState(in ShaderEntry shader);

    /// <summary>
    /// Room for <paramref name="bytes"/> in this frame's staging ring, to fill and copy from in this frame's commands;
    /// <c>default</c>, with <see cref="GpuBuffer.None"/> and no bytes, when the ring cannot grow to hold them.
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
    /// the barriers its uses need, submits them, shows the window's image, frees what no frame has asked for in a few,
    /// and begins the next frame, waiting for the one two before and taking the window's next image. Core calls it; a
    /// gem never does.
    /// </summary>
    void Present(in GpuCommands commands);
}

/// <summary>
/// Room in this frame's staging ring to write into, and where the GPU reads it from: the buffer and offset a copy
/// command names. Good for the frame it was begun in and gone after.
/// </summary>
public readonly ref struct GpuUpload(GpuBuffer buffer, ulong offset, Span<byte> bytes)
{
    public GpuBuffer Buffer { get; } = buffer;

    public ulong Offset { get; } = offset;

    public Span<byte> Bytes { get; } = bytes;
}
