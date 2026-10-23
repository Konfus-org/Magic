using System.Drawing;
using Magic.Contexts.Frames;
using Magic.Interfaces;

namespace Magic.Contexts.Gpu;

/// <summary>
/// What an overlay script (<see cref="IOverlayScript"/>) is given to record with: the frame's commands, the target it
/// draws over and the rectangle of it, the target's format, which a pipeline drawing into it is made for, and its
/// encoding, which what is drawn is encoded as, and the frame. <see cref="Begin"/> opens a render pass into the target,
/// keeping what is there, with the viewport and scissor set to the rectangle, and hands it back for the script to draw
/// in and dispose, a <c>using</c> block. Whatever must be uploaded is uploaded before <see cref="Begin"/>, as an upload
/// cannot go in a render pass.
/// </summary>
public readonly record struct OverlayContext(
    GpuCommands Commands,
    GpuTexture Target,
    Rectangle Area,
    GpuFormat Format,
    TargetEncoding Encoding,
    Frame Frame)
{
    public GpuRenderPass Begin()
        => Commands.RenderPass([new GpuTextureLevel(Target)], GpuContents.Keep).Viewport(Area).Scissor(Area);
}
