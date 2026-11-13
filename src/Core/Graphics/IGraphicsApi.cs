using Magic.Utils;

namespace Magic.Graphics;

/// <summary>
/// The GPU: resources made by name into one bindless heap, and one <see cref="GpuCommands"/> a frame, submitted whole.
/// </summary>
/// <remarks>
/// Gems record their work and ask for resources on <see cref="RenderGraph"/>, and Core presents each frame. A gem calls
/// the provider itself only for what it has and for what a frame gives back. Call members on the render thread, unless
/// a member says otherwise.
/// </remarks>
public interface IGraphicsApi
{
    GpuFeatures Features { get; }

    /// <summary>
    /// The window's image for this frame, to draw into until <see cref="Present"/> shows it.
    /// </summary>
    /// <remarks>
    /// It is a new texture each frame, and <see cref="GpuTexture.None"/> when headless or while the window is
    /// minimised. Gems ask for it through <see cref="RenderGraph.WindowTarget"/>.
    /// </remarks>
    GpuTexture WindowTarget { get; }

    TextureDescription GetDescription(GpuTexture texture);

    /// <summary>
    /// Gets the texture under the description's name, made on the first ask.
    /// </summary>
    /// <remarks>
    /// While the description stays the same, each ask gives the same texture, so its contents carry from frame to
    /// frame.
    /// A changed description makes a new texture and destroys the old one. A texture that no frame asked for in a few
    /// frames is destroyed. Gems ask through <see cref="RenderGraph.Texture"/>.
    /// </remarks>
    Result<GpuTexture> GetTexture(in TextureDescription description);

    /// <summary>
    /// Gets the buffer under the description's name, kept as <see cref="GetTexture"/> keeps a texture.
    /// </summary>
    /// <remarks>
    /// When only the size changes, the new buffer gets as much of the old contents as fits, before the frame's passes.
    /// Gems ask through <see cref="RenderGraph.Buffer"/>.
    /// </remarks>
    Result<GpuBuffer> GetBuffer(in BufferDescription description);

    /// <summary>
    /// Gets the bottom-level acceleration structure under the description's name, kept as
    /// <see cref="GetTexture"/> keeps a texture.
    /// </summary>
    /// <remarks>
    /// Call it only when <see cref="GpuFeatures.HasRayQuery"/> is true. The handle holds the device address. Gems ask
    /// through <see cref="RenderGraph.Acceleration(in BottomLevelDescription)"/>.
    /// </remarks>
    Result<GpuAcceleration> GetAcceleration(in BottomLevelDescription description);

    /// <summary>
    /// Gets the top-level acceleration structure under the description's name, kept as <see cref="GetTexture"/> keeps a
    /// texture.
    /// </summary>
    /// <remarks>
    /// Call it only when <see cref="GpuFeatures.HasRayQuery"/> is true. Gems ask through
    /// <see cref="RenderGraph.Acceleration(in TopLevelDescription)"/>.
    /// </remarks>
    Result<GpuAcceleration> GetAcceleration(in TopLevelDescription description);

    /// <summary>
    /// Gets the sampler for <paramref name="description"/>: the same one for each ask with the same description.
    /// </summary>
    /// <remarks>
    /// It is kept as <see cref="GetTexture"/> keeps a texture. Gems ask through <see cref="RenderGraph.Sampler"/>.
    /// </remarks>
    Result<GpuSampler> GetSampler(in SamplerDescription description);

    /// <summary>
    /// Whether <paramref name="shader"/> is built for this GPU. The first ask starts to compile it.
    /// </summary>
    /// <remarks>
    /// A compute entry is built into its pipeline; a vertex or pixel entry is compiled. Gems ask through
    /// <see cref="RenderGraph.GetState(in ShaderEntry)"/>.
    /// </remarks>
    GpuState GetState(in ShaderEntry shader);

    /// <summary>
    /// Gets room for <paramref name="bytes"/> in this frame's staging ring, to fill and copy from in this frame's
    /// commands.
    /// </summary>
    /// <remarks>
    /// Gives <c>default</c>, with <see cref="GpuBuffer.None"/> and no bytes, when the ring cannot grow to hold them.
    /// </remarks>
    GpuUpload BeginUpload(int bytes);

    /// <summary>
    /// Reads the bytes of a <see cref="BufferUsages.Readback"/> buffer after the frame recorded at the call finishes on
    /// the GPU. Any thread.
    /// </summary>
    Task<Result<ReadOnlyMemory<byte>>> ReadBackAsync(
        GpuBuffer buffer,
        long offset,
        int bytes,
        CancellationToken cancel
    );

    /// <summary>
    /// Submits the frame's passes, shows the window's image and begins the next frame. Only Core calls it.
    /// </summary>
    /// <remarks>
    /// It frees what no frame asked for in a few frames. The next frame waits for the frame two before it.
    /// </remarks>
    void Present(in GpuCommands commands);
}

/// <summary>
/// Room in this frame's staging ring to write into, and the buffer and offset that a copy command reads it from.
/// </summary>
/// <remarks>
/// Use it only in the frame that it was begun in.
/// </remarks>
public readonly ref struct GpuUpload(GpuBuffer buffer, ulong offset, Span<byte> bytes)
{
    public GpuBuffer Buffer { get; } = buffer;

    public ulong Offset { get; } = offset;

    public Span<byte> Bytes { get; } = bytes;
}
