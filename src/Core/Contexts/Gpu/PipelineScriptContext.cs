using System.Drawing;
using Magic.Contexts.Frames;
using Magic.Interfaces;

namespace Magic.Contexts.Gpu;

/// <summary>
/// What a pipeline script (<see cref="IPipelineScript"/>) is given to record with: the frame's commands, the target its
/// pass draws into and the rectangle of it, the target's format, which a pipeline drawing into it is made for, and its
/// encoding, which what is drawn is encoded as, and the frame. <see cref="Begin"/> opens a render pass into the target
/// as the pass's file says, with the viewport and scissor set to the rectangle, and hands it back for the script to
/// draw in and dispose, a <c>using</c> block. Whatever must be uploaded is uploaded before <see cref="Begin"/>, as an
/// upload cannot go in a render pass.
/// </summary>
public readonly record struct PipelineScriptContext(GpuCommands Commands, GpuTexture Target, Rectangle Area, GpuFormat Format, TargetEncoding Encoding, GpuContents Contents, Frame Frame)
{
    public GpuRenderPass Begin() => Commands.RenderPass([new GpuTextureLevel(Target)], Contents).Viewport(Area).Scissor(Area);
}
