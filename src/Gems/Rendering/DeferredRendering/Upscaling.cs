using Magic.DeferredRendering.View;
using Magic.Graphics;

namespace Magic.DeferredRendering;

/// <summary>
/// The passes that take the view's finished picture from the render size to the size it is shown at.
/// </summary>
/// <remarks>
/// A gem that upscales makes it each frame, before the view. The renderer calls <paramref name="Record"/> once the
/// output is drawn, and shows the picture it returns in place of the output, stretched, which it shows when that is
/// none. While one is made, the camera jitters.
/// </remarks>
public readonly record struct Upscaling(Func<RenderGraph, GpuView, GpuTexture> Record);
