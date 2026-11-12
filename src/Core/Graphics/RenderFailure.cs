namespace Magic.Graphics;

/// <summary>
/// What failed in a frame, as told to <see cref="RenderGraph.Fail"/>. The frame goes on without it.
/// </summary>
public enum RenderFailure : byte
{
    /// <summary>
    /// A part of the frame that is not recorded, or a gem's whole part of it.
    /// </summary>
    Pass,

    /// <summary>
    /// A texture, buffer or other GPU resource that the frame could not get.
    /// </summary>
    Resource,

    /// <summary>
    /// A shader that did not build.
    /// </summary>
    Shader,

    /// <summary>
    /// The sky, which is not drawn.
    /// </summary>
    Sky,

    /// <summary>
    /// The weather, which is not drawn.
    /// </summary>
    Weather,

    /// <summary>
    /// A post effect, which the frame skips.
    /// </summary>
    PostEffect,
}
