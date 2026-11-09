namespace Magic.Graphics;

/// <summary>
/// How a pass touches a resource, which a transition names before and after: enough for the GPU provider to put the
/// right barrier and image layout between two passes, and nothing of the API's own words for them.
/// </summary>
public enum GpuAccess : byte
{
    /// <summary>
    /// Not touched yet, or its contents not needed: what a texture is in before its first use, and what the window's
    /// target is in as a frame begins.
    /// </summary>
    None,

    /// <summary>
    /// Read by shaders, sampled or loaded.
    /// </summary>
    ShaderRead,

    /// <summary>
    /// Written, and maybe read, by shaders: a storage buffer or image.
    /// </summary>
    ShaderWrite,

    /// <summary>
    /// Drawn into as a colour target.
    /// </summary>
    ColorTarget,

    /// <summary>
    /// Drawn into as the depth target.
    /// </summary>
    DepthTarget,

    /// <summary>
    /// Read as a depth target that is tested against but not written, and sampled at the same time.
    /// </summary>
    DepthRead,

    /// <summary>
    /// Read as the arguments of an indirect dispatch or draw.
    /// </summary>
    Indirect,

    /// <summary>
    /// Read as the index buffer of a draw.
    /// </summary>
    Index,

    CopySource,

    CopyDestination,

    /// <summary>
    /// Handed to the window at the frame's end.
    /// </summary>
    Present,

    /// <summary>
    /// Read by the CPU, through <see cref="IGraphicsApi.ReadBackAsync"/>.
    /// </summary>
    HostRead,
}
