using Magic.Graphics;

namespace Magic.GpuWorld;

/// <summary>
/// What a gem that upscales makes each frame, before the view is: the passes that take the finished picture, the view's
/// output at the render size, to the size it is shown at. The renderer calls <paramref name="Record"/> once the output is
/// drawn, and shows the picture it returns in place of the output, stretched, which it shows when that is none. While
/// one is made, the camera jitters.
/// </summary>
public readonly record struct Upscaling(Func<RenderGraph, GpuView, GpuTexture> Record);
