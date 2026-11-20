using Magic.Utils;

namespace Magic.Graphics;

/// <summary>
/// The GPU: resources made by name into one bindless heap, and one <see cref="GpuCommands"/> a frame, submitted whole.
/// </summary>
/// <remarks>
/// Gems record their work on <see cref="RenderGraph"/>, and Core presents each frame. A gem asks
/// the provider for its resources, for what it has, for the state of a shader, for the description of a texture, and
/// for what a frame gives back. Call members on the render thread, unless a member says otherwise.
/// </remarks>
public interface IGraphicsApi
{
    GpuFeatures Features { get; }

    /// <summary>
    /// The window's image for this frame, to draw into until <see cref="Present"/> shows it.
    /// </summary>
    /// <remarks>
    /// It is a new texture each frame, and <see cref="GpuTexture.None"/> when headless or while the window is
    /// minimised.
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
    /// frames is destroyed. Ask in each frame that uses it. The heap id is known at once, to write into constants.
    /// </remarks>
    Result<GpuTexture> GetTexture(in TextureDescription description);

    /// <summary>
    /// Gets the texture of <paramref name="view"/> under the description's name: each view of the frame has its own.
    /// </summary>
    Result<GpuTexture> GetTexture(in TextureDescription description, int view) =>
        GetTexture(description with { Name = ViewNames.Get(description.Name, view) });

    /// <summary>
    /// Gets the buffer under the description's name, kept as <see cref="GetTexture(in TextureDescription)"/> keeps a texture.
    /// </summary>
    /// <remarks>
    /// When only the size changes, the new buffer gets as much of the old contents as fits, before the frame's passes.
    /// </remarks>
    Result<GpuBuffer> GetBuffer(in BufferDescription description);

    /// <summary>
    /// Gets the buffer of <paramref name="view"/> under the description's name: each view of the frame has its own.
    /// </summary>
    Result<GpuBuffer> GetBuffer(in BufferDescription description, int view) =>
        GetBuffer(description with { Name = ViewNames.Get(description.Name, view) });

    /// <summary>
    /// Gets the bottom-level acceleration structure under the description's name, kept as
    /// <see cref="GetTexture(in TextureDescription)"/> keeps a texture.
    /// </summary>
    /// <remarks>
    /// Call it only when <see cref="GpuFeatures.HasRayQuery"/> is true. The handle holds the device address.
    /// </remarks>
    Result<GpuAcceleration> GetAcceleration(in BottomLevelDescription description);

    /// <summary>
    /// Gets the bottom-level acceleration structure of <paramref name="view"/> under the description's name.
    /// </summary>
    Result<GpuAcceleration> GetAcceleration(in BottomLevelDescription description, int view) =>
        GetAcceleration(description with { Name = ViewNames.Get(description.Name, view) });

    /// <summary>
    /// Gets the top-level acceleration structure under the description's name, kept as <see cref="GetTexture(in TextureDescription)"/> keeps a
    /// texture.
    /// </summary>
    /// <remarks>
    /// Call it only when <see cref="GpuFeatures.HasRayQuery"/> is true.
    /// </remarks>
    Result<GpuAcceleration> GetAcceleration(in TopLevelDescription description);

    /// <summary>
    /// Gets the top-level acceleration structure of <paramref name="view"/> under the description's name.
    /// </summary>
    Result<GpuAcceleration> GetAcceleration(in TopLevelDescription description, int view) =>
        GetAcceleration(description with { Name = ViewNames.Get(description.Name, view) });

    /// <summary>
    /// Gets the sampler for <paramref name="description"/>: the same one for each ask with the same description.
    /// </summary>
    /// <remarks>
    /// It is kept as <see cref="GetTexture(in TextureDescription)"/> keeps a texture.
    /// </remarks>
    Result<GpuSampler> GetSampler(in SamplerDescription description);

    /// <summary>
    /// Whether <paramref name="shader"/> is built for this GPU. The first ask starts to compile it.
    /// </summary>
    /// <remarks>
    /// A compute entry is built into its pipeline; a vertex or pixel entry is compiled.
    /// </remarks>
    GpuState GetState(in ShaderEntry shader);

    /// <summary>
    /// Gets the state of all <paramref name="shaders"/>: failed if any failed, loading while any loads, else ready.
    /// </summary>
    /// <remarks>
    /// Each is asked, so the first ask starts all of their builds.
    /// </remarks>
    GpuState GetState(params ReadOnlySpan<ShaderEntry> shaders)
    {
        GpuState all = GpuState.Ready;
        foreach (ShaderEntry shader in shaders)
        {
            GpuState state = GetState(shader);
            if (state == GpuState.Failed)
                all = GpuState.Failed;
            else if (state == GpuState.Loading && all == GpuState.Ready)
                all = GpuState.Loading;
        }

        return all;
    }

    /// <summary>
    /// Whether one of <paramref name="shaders"/> still builds. It stops at the first that does.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="GetState(ReadOnlySpan{ShaderEntry})"/>, a failed shader does not hide one that still builds.
    /// </remarks>
    bool IsAnyLoading(params ReadOnlySpan<ShaderEntry> shaders)
    {
        foreach (ShaderEntry shader in shaders)
        {
            if (GetState(shader) == GpuState.Loading)
                return true;
        }

        return false;
    }

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
    public ulong Offset { get; } = offset;

    public GpuBuffer Buffer { get; } = buffer;

    public Span<byte> Bytes { get; } = bytes;
}
