using Magic.Assets;
using Magic.Graphics;

namespace Magic.DeferredRendering;

/// <summary>
/// The finished picture of one view, which the shading step makes for each view. Other gems take it to send it or save
/// it.
/// </summary>
/// <param name="Texture">The picture. It can be smaller than the shown size, and in a format of more than 8 bits.</param>
/// <param name="Width">The width that the view is shown at, in pixels.</param>
/// <param name="Height">The height that the view is shown at, in pixels.</param>
/// <param name="Target">The render texture that the camera of the view draws into, or none for the window.</param>
public readonly record struct FinalImage(GpuTexture Texture, int Width, int Height, Handle<RenderTexture> Target);
