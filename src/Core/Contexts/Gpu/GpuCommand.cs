using Magic.Contexts.Assets;
using Rectangle = System.Drawing.Rectangle;

namespace Magic.Contexts.Gpu;

/// <summary>
/// One GPU command, as <see cref="GpuCommands"/> records it: plain data whose <see cref="Type"/> says which fields mean
/// something; the rest are default. A run, of bindings or of bytes, is a slice of the list's own
/// (<see cref="GpuCommands.BindingsOf"/>, <see cref="GpuCommands.BytesOf"/>).
/// </summary>
public readonly record struct GpuCommand(
    GpuCommandType Type,
    GpuStage Stage = GpuStage.Vertex,
    GpuPipeline Pipeline = default,
    GpuBuffer Buffer = default,
    GpuTexture Depth = default,
    uint DepthLayer = 0,
    GpuLoad Load = GpuLoad.Load,
    Color Clear = default,
    Rectangle Area = default,
    uint Slot = 0,
    int RunStart = 0,
    int RunLength = 0,
    uint Count = 0,
    uint Instances = 1,
    uint First = 0,
    int VertexOffset = 0,
    uint FirstInstance = 0,
    uint Offset = 0,
    uint GroupsX = 0,
    uint GroupsY = 0,
    uint GroupsZ = 0,
    bool Wide = false);

/// <summary>
/// What a <see cref="GpuCommand"/> does; each says which fields it uses.
/// </summary>
public enum GpuCommandType : byte
{
    /// <summary>
    /// The run: the colour targets, none for a depth-only pass. <see cref="GpuCommand.Depth"/> (at
    /// <see cref="GpuCommand.DepthLayer"/>) or none; <see cref="GpuCommand.Load"/> for all of them, a clear to
    /// <see cref="GpuCommand.Clear"/> and depth to 0, reverse-Z's far.
    /// </summary>
    BeginRenderPass,

    EndRenderPass,

    /// <summary>
    /// The run: the buffers, and the textures as storage, the pass writes.
    /// </summary>
    BeginComputePass,

    EndComputePass,

    /// <summary>
    /// <see cref="GpuCommand.Area"/>, in target pixels.
    /// </summary>
    SetViewport,

    /// <summary>
    /// Area, in target pixels.
    /// </summary>
    SetScissor,

    /// <summary>
    /// <see cref="GpuCommand.Pipeline"/>: a graphics one inside a render pass, a compute one inside a compute pass.
    /// </summary>
    BindPipeline,

    /// <summary>
    /// <see cref="GpuCommand.Slot"/>, the first slot; the run, the buffers.
    /// </summary>
    BindVertexBuffers,

    /// <summary>
    /// <see cref="GpuCommand.Buffer"/>, with 32-bit indices when <see cref="GpuCommand.Wide"/>, else 16-bit.
    /// </summary>
    BindIndexBuffer,

    /// <summary>
    /// <see cref="GpuCommand.Stage"/> and Slot; the run, the read-only storage buffers.
    /// </summary>
    BindStorageBuffers,

    /// <summary>
    /// Stage and Slot; the run, the read-only storage textures.
    /// </summary>
    BindStorageTextures,

    /// <summary>
    /// Stage and Slot; the run, the sampled textures, each with its sampler.
    /// </summary>
    BindTextures,

    /// <summary>
    /// Stage; the run, the bytes of the stage's one constant block.
    /// </summary>
    PushConstants,

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
    /// Count indexed draws, read from Buffer at <see cref="GpuCommand.Offset"/>.
    /// </summary>
    DrawIndexedIndirect,

    /// <summary>
    /// <see cref="GpuCommand.GroupsX"/> by <see cref="GpuCommand.GroupsY"/> by <see cref="GpuCommand.GroupsZ"/> groups.
    /// </summary>
    Dispatch,

    /// <summary>
    /// The groups, read from Buffer at Offset.
    /// </summary>
    DispatchIndirect,

    /// <summary>
    /// Outside any pass. The run: the source and destination textures, each at a level and layer;
    /// <see cref="GpuCommand.Area"/>'s width and height, the texels copied from each one's corner.
    /// </summary>
    CopyTexture,

    /// <summary>
    /// Outside any pass. The run: the source and destination buffers; <see cref="GpuCommand.Count"/> bytes from
    /// <see cref="GpuCommand.First"/> in the source to <see cref="GpuCommand.Offset"/> in the destination.
    /// </summary>
    CopyBuffer,

    /// <summary>
    /// Outside any pass. The run: the source and destination textures, each a whole level of a layer, the source scaled
    /// to the destination, filtered as <see cref="GpuCommand.Slot"/> says (a <see cref="GpuFilter"/>).
    /// </summary>
    Blit,

    /// <summary>
    /// Outside any pass. The run: the texture, whose levels past the first are made from it.
    /// </summary>
    GenerateMipmaps,

    /// <summary>
    /// Outside any pass. The run of bytes: the group's name, UTF-8; what a GPU debugger shows the commands up to the
    /// matching <see cref="PopDebugGroup"/> under.
    /// </summary>
    PushDebugGroup,

    /// <summary>
    /// Outside any pass: ends the last group pushed.
    /// </summary>
    PopDebugGroup,
}

/// <summary>
/// What a render pass does with its targets' contents when it starts.
/// </summary>
public enum GpuLoad : byte
{
    /// <summary>
    /// Keeps them, to draw over.
    /// </summary>
    Load,

    /// <summary>
    /// Clears them.
    /// </summary>
    Clear,

    /// <summary>
    /// Leaves them undefined: for a pass that covers every pixel.
    /// </summary>
    DontCare,
}
