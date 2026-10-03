using System.Drawing;
using System.Numerics;

namespace Magic.Contexts.Rendering;

/// <summary>
/// What a <see cref="RenderCommand"/> does; the comment on each says which fields it uses.
/// </summary>
public enum RenderCommandType : byte
{
    /// <summary>
    /// <see cref="RenderCommand.Texture"/> = colour target, or Run = the colour targets when there are several,
    /// <see cref="RenderCommand.Depth"/> = depth target or none, <see cref="RenderCommand.Load"/> for all of them,
    /// <see cref="RenderCommand.ClearColor"/> (depth clears to 0, reverse-Z far).
    /// </summary>
    BeginRenderPass,

    EndRenderPass,

    /// <summary>
    /// The run (<see cref="RenderCommand.Run"/>) of buffers and textures the pass writes.
    /// </summary>
    BeginComputePass,

    EndComputePass,

    /// <summary>
    /// <see cref="RenderCommand.Rect"/>, in target pixels.
    /// </summary>
    SetViewport,

    /// <summary>
    /// Rect.
    /// </summary>
    SetScissor,

    /// <summary>
    /// <see cref="RenderCommand.Pipeline"/>, graphics inside a render pass, compute inside a compute pass.
    /// </summary>
    BindPipeline,

    /// <summary>
    /// <see cref="RenderCommand.Slot"/> = first slot, Run = the buffers.
    /// </summary>
    BindVertexBuffers,

    /// <summary>
    /// <see cref="RenderCommand.Buffer"/>, <see cref="RenderCommand.Wide"/> = 32-bit indices (else 16).
    /// </summary>
    BindIndexBuffer,

    /// <summary>
    /// <see cref="RenderCommand.Stage"/>, Slot, Run = the read-only buffers.
    /// </summary>
    BindStorageBuffers,

    /// <summary>
    /// Stage, Slot, Run = the textures, each with its sampler.
    /// </summary>
    BindTextures,

    /// <summary>
    /// Stage, Run = the bytes (in <see cref="RenderCommands.Bytes"/>) of the stage's one constant block.
    /// </summary>
    PushConstants,

    /// <summary>
    /// <see cref="RenderCommand.Count"/> vertices from <see cref="RenderCommand.First"/>, <see cref="RenderCommand.Instances"/>.
    /// </summary>
    Draw,

    /// <summary>
    /// Count indices from First, <see cref="RenderCommand.VertexOffset"/> added, Instances from <see cref="RenderCommand.FirstInstance"/>.
    /// </summary>
    DrawIndexed,

    /// <summary>
    /// Count indexed draws read from Buffer at <see cref="RenderCommand.Offset"/>.
    /// </summary>
    DrawIndexedIndirect,

    /// <summary>
    /// <see cref="RenderCommand.Groups"/>.
    /// </summary>
    Dispatch,

    /// <summary>
    /// The groups read from Buffer at Offset.
    /// </summary>
    DispatchIndirect,

    /// <summary>
    /// <see cref="RenderCommand.Rect"/> of Texture (level 0, layer 0) to the <see cref="RenderCommand.Destination"/> region,
    /// filtered linearly; a region of size 0 is the destination's whole level. Outside any pass.
    /// </summary>
    Blit,
}

/// <summary>
/// One GPU command, like an <see cref="Events.Event"/>: plain data whose <see cref="Type"/> says which fields mean
/// something; the rest are default. A run (bindings or bytes) is a slice of its <see cref="RenderCommands"/>.
/// </summary>
public readonly record struct RenderCommand(
    RenderCommandType Type,
    GpuStage Stage = GpuStage.Vertex,
    GpuPipeline Pipeline = default,
    GpuBuffer Buffer = default,
    GpuTexture Texture = default,
    GpuTexture Depth = default,
    TextureRegion Destination = default,
    GpuLoad Load = GpuLoad.Load,
    Vector4 ClearColor = default,
    Rectangle Rect = default,
    uint Slot = 0,
    (int Start, int Length) Run = default,
    uint Count = 0,
    uint Instances = 1,
    uint First = 0,
    int VertexOffset = 0,
    uint FirstInstance = 0,
    uint Offset = 0,
    (uint X, uint Y, uint Z) Groups = default,
    bool Wide = false);
