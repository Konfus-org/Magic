using Magic.Contexts.Assets;
using Rectangle = System.Drawing.Rectangle;

namespace Magic.Contexts.Gpu;

/// <summary>
/// What a <see cref="GpuCommand"/> does; each says which fields and which run it uses.
/// </summary>
public enum GpuCommandType : byte
{
    /// <summary>
    /// The run of levels: the colour targets, none for a depth-only pass. <see cref="GpuCommand.Depth"/>, or none;
    /// <see cref="GpuCommand.Contents"/> for all of them, a clear to <see cref="GpuCommand.Clear"/> and depth to 0,
    /// reverse-Z's far.
    /// </summary>
    BeginRenderPass,

    EndRenderPass,

    /// <summary>
    /// The run of levels: the textures the pass writes; the second run, of buffers: the buffers it writes.
    /// </summary>
    BeginComputePass,

    EndComputePass,

    /// <summary>
    /// <see cref="GpuCommand.Area"/>, in target pixels.
    /// </summary>
    Viewport,

    /// <summary>
    /// Area, in target pixels.
    /// </summary>
    Scissor,

    /// <summary>
    /// <see cref="GpuCommand.Pipeline"/>: a graphics one inside a render pass, a compute one inside a compute pass.
    /// </summary>
    Pipeline,

    /// <summary>
    /// The run of buffers: the vertex buffers, from slot 0.
    /// </summary>
    VertexBuffers,

    /// <summary>
    /// <see cref="GpuCommand.Buffer"/>, of indices of <see cref="GpuCommand.IndexSize"/>.
    /// </summary>
    IndexBuffer,

    /// <summary>
    /// <see cref="GpuCommand.Stage"/>; the run of buffers: what it reads, from register 0.
    /// </summary>
    Buffers,

    /// <summary>
    /// Stage; the run of textures: what it reads texel by texel, with no sampler, from register 0.
    /// </summary>
    RawTextures,

    /// <summary>
    /// Stage; the run of sampled textures: what it reads through a sampler, from register 0.
    /// </summary>
    Textures,

    /// <summary>
    /// Stage; the run of bytes: the stage's one constant block.
    /// </summary>
    Constants,

    /// <summary>
    /// <see cref="GpuCommand.Count"/> vertices from <see cref="GpuCommand.First"/>, <see cref="GpuCommand.Instances"/>
    /// from <see cref="GpuCommand.FirstInstance"/>.
    /// </summary>
    Draw,

    /// <summary>
    /// Count indices from First, <see cref="GpuCommand.VertexOffset"/> added to each, Instances from FirstInstance.
    /// </summary>
    DrawIndexed,

    /// <summary>
    /// Count indexed draws, their arguments read from Buffer at <see cref="GpuCommand.Offset"/>.
    /// </summary>
    DrawIndexedFromBuffer,

    /// <summary>
    /// <see cref="GpuCommand.GroupsX"/> by <see cref="GpuCommand.GroupsY"/> by <see cref="GpuCommand.GroupsZ"/> groups.
    /// </summary>
    Dispatch,

    /// <summary>
    /// The groups, read from Buffer at Offset.
    /// </summary>
    DispatchFromBuffer,

    /// <summary>
    /// Outside any pass. The run of regions: the source, whose size is copied, and the destination.
    /// </summary>
    CopyTexture,

    /// <summary>
    /// Outside any pass. The run of buffers: the source and destination; <see cref="GpuCommand.Count"/> bytes from
    /// <see cref="GpuCommand.First"/> in the source to <see cref="GpuCommand.Offset"/> in the destination.
    /// </summary>
    CopyBuffer,

    /// <summary>
    /// Outside any pass. <see cref="GpuCommand.Texture"/>, whose levels past the first are made from it.
    /// </summary>
    GenerateMipmaps,

    /// <summary>
    /// Outside any pass. The run of bytes: the group's name, UTF-8; what a GPU debugger shows the commands up to the
    /// matching <see cref="EndDebugGroup"/> under.
    /// </summary>
    BeginDebugGroup,

    /// <summary>
    /// Outside any pass: ends the last group begun.
    /// </summary>
    EndDebugGroup,
}

/// <summary>
/// What a render pass does with what its targets hold when it starts.
/// </summary>
public enum GpuContents : byte
{
    /// <summary>
    /// Keeps it, to draw over.
    /// </summary>
    Keep,

    /// <summary>
    /// Clears it.
    /// </summary>
    Clear,

    /// <summary>
    /// Throws it away, leaving the targets undefined: for a pass that covers every pixel.
    /// </summary>
    Discard,
}

/// <summary>
/// How wide each index of an index buffer is.
/// </summary>
public enum GpuIndexSize : byte
{
    Bits16,
    Bits32,
}

/// <summary>
/// One GPU command, as <see cref="GpuCommands"/> records it: plain data whose <see cref="Type"/> says which fields mean
/// something; the rest are default. A run (of texture levels, sampled textures, textures, buffers, regions or bytes)
/// is a slice of the list's own, which the list hands back by the command (<see cref="GpuCommands.GetLevels"/> and
/// the others like it).
/// </summary>
public readonly record struct GpuCommand(
    GpuCommandType Type,
    GpuStage Stage = GpuStage.Vertex,
    GpuPipeline Pipeline = default,
    GpuBuffer Buffer = default,
    GpuTexture Texture = default,
    GpuTextureLevel Depth = default,
    GpuContents Contents = GpuContents.Keep,
    Color Clear = default,
    Rectangle Area = default,
    GpuIndexSize IndexSize = GpuIndexSize.Bits16,
    int RunStart = 0,
    int RunLength = 0,
    int SecondRunStart = 0,
    int SecondRunLength = 0,
    uint Count = 0,
    uint Instances = 1,
    uint First = 0,
    int VertexOffset = 0,
    uint FirstInstance = 0,
    uint Offset = 0,
    uint GroupsX = 0,
    uint GroupsY = 0,
    uint GroupsZ = 0);
