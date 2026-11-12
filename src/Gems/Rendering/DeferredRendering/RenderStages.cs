using Magic.Graphics;

namespace Magic.DeferredRendering;

/// <summary>
/// The renderer's stages between <see cref="RenderStage.Prepare"/> and <see cref="RenderStage.Overlay"/>, in the order
/// they run.
/// </summary>
public static class RenderStages
{
    /// <summary>
    /// What the view sees: depth, the GBuffer and the glass targets.
    /// </summary>
    public static readonly RenderStage Geometry = new(1);

    /// <summary>
    /// What lights the view's surfaces: each tile's lights, shadows and indirect light.
    /// </summary>
    public static readonly RenderStage Light = new(2);

    /// <summary>
    /// The view's surfaces shaded into the picture.
    /// </summary>
    public static readonly RenderStage Shade = new(3);

    /// <summary>
    /// What finishes the picture, and its copy to the window or a screenshot.
    /// </summary>
    public static readonly RenderStage Post = new(4);
}
