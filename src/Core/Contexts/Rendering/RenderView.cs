using System.Drawing;
using Magic.Contexts.Gpu;

namespace Magic.Contexts.Rendering;

/// <summary>
/// One camera as a frame draws it: <paramref name="Id"/>, the same from frame to frame while the camera is; the size
/// it is drawn at, <paramref name="Size"/>, and the size it is shown at, <paramref name="OutputSize"/>, its part of its
/// target; <paramref name="Constants"/>, the buffer of this frame's view constants, which the renderer names in the
/// header of each pass drawn for the view (Shaders/Core/Pass.slang); and <paramref name="Exposure"/>, the view's
/// exposure, which the renderer keeps from frame to frame, which lighting and the sky are pre-exposed by, and which an
/// exposure effect writes for the frames after.
/// </summary>
public sealed record RenderView(int Id, Size Size, Size OutputSize, GpuBuffer Constants, GpuBuffer Exposure);
