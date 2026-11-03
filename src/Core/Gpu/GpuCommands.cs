using System.Collections.Immutable;
using System.Runtime.InteropServices;

namespace Magic.Gpu;

/// <summary>
/// A frame's commands as a value: passes in order, each with the transitions it needs first. A gem records one with
/// <see cref="GpuCommandRecorder"/> and hands it to <see cref="IGraphicsApi.Submit"/>; the GPU provider turns it into
/// its API's calls and nothing else decides what the GPU does.
/// </summary>
public readonly record struct GpuCommands(ImmutableArray<GpuPass> Passes)
{
    public static GpuCommands None => new([]);
}

/// <summary>
/// What a pass does, which picks the commands it may hold.
/// </summary>
public enum GpuPassKind : byte
{
    /// <summary>
    /// Dispatches, with the pipelines and constants they take.
    /// </summary>
    Compute,

    /// <summary>
    /// Draws into <see cref="GpuPass.Targets"/>, with the pipelines, constants, viewport and index buffer they take.
    /// </summary>
    Draw,

    /// <summary>
    /// Copies, clears and fills.
    /// </summary>
    Transfer,
}

/// <summary>
/// One pass: a debug group the GPU provider times, its transitions put before anything in it runs, and its commands.
/// </summary>
public readonly record struct GpuPass
{
    public GpuPass() { }

    public required string Name { get; init; }

    public required GpuPassKind Kind { get; init; }

    public ImmutableArray<GpuTransition> Transitions { get; init; } = [];

    public ImmutableArray<GpuCommand> Commands { get; init; } = [];

    /// <summary>
    /// What a <see cref="GpuPassKind.Draw"/> pass draws into.
    /// </summary>
    public GpuRenderTargets Targets { get; init; }
}

/// <summary>
/// A resource's access changing between two passes, which the GPU provider makes a barrier of. A texture's covers one
/// level, or every level when <see cref="Level"/> is -1.
/// </summary>
public readonly record struct GpuTransition
{
    public GpuTransition() { }

    /// <summary>
    /// The buffer's or texture's id, as <see cref="IsTexture"/> says.
    /// </summary>
    public required uint Id { get; init; }

    public required bool IsTexture { get; init; }

    public required GpuAccess Before { get; init; }

    public required GpuAccess After { get; init; }

    public int Level { get; init; } = -1;
}

/// <summary>
/// Whether a draw pass keeps, clears or ignores what a target holds as it begins.
/// </summary>
public enum TargetLoad : byte
{
    Keep,
    Clear,

    /// <summary>
    /// Whatever it holds, which is fastest when every pixel is drawn.
    /// </summary>
    Discard,
}

/// <summary>
/// One colour target of a draw pass: a texture's level, and what to do with what it holds.
/// </summary>
public readonly record struct GpuColorTarget
{
    public GpuColorTarget() { }

    public required GpuTexture Texture { get; init; }

    public int Level { get; init; }

    public TargetLoad Load { get; init; } = TargetLoad.Keep;

    /// <summary>
    /// What <see cref="TargetLoad.Clear"/> clears to, as four floats.
    /// </summary>
    public GpuConstants ClearColor { get; init; }
}

/// <summary>
/// The depth target of a draw pass: a texture's level, and what to do with what it holds.
/// </summary>
public readonly record struct GpuDepthTarget
{
    public GpuDepthTarget() { }

    public required GpuTexture Texture { get; init; }

    public int Level { get; init; }

    public TargetLoad Load { get; init; } = TargetLoad.Keep;

    /// <summary>
    /// What <see cref="TargetLoad.Clear"/> clears to; 0 is infinitely far, as depth is reversed.
    /// </summary>
    public float ClearDepth { get; init; }
}

/// <summary>
/// What a draw pass draws into: up to 8 colour targets of one size, and a depth target or none.
/// </summary>
public readonly record struct GpuRenderTargets
{
    public GpuRenderTargets() { }

    public ImmutableArray<GpuColorTarget> Colors { get; init; } = [];

    public GpuDepthTarget? Depth { get; init; }
}

/// <summary>
/// The 16 bytes a pass pushes to its shaders, read there as <c>Core/Pass.slang</c>'s header with the same four names:
/// what a small pass needs, or in <see cref="X"/> the heap id of the buffer its constants struct is in. Also a colour,
/// as a clear's. Built from unsigned integers or floats, whichever the shader reads.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct GpuConstants(uint X, uint Y = 0, uint Z = 0, uint W = 0)
{
    public static GpuConstants FromFloats(float x, float y = 0, float z = 0, float w = 0) =>
        new(
            BitConverter.SingleToUInt32Bits(x),
            BitConverter.SingleToUInt32Bits(y),
            BitConverter.SingleToUInt32Bits(z),
            BitConverter.SingleToUInt32Bits(w)
        );
}

/// <summary>
/// Which command a <see cref="GpuCommand"/> is; the fields each reads are on <see cref="GpuCommandRecorder"/>'s
/// method of the same name.
/// </summary>
public enum GpuCommandKind : byte
{
    BindPipeline,
    PushConstants,
    Dispatch,
    DispatchIndirect,
    SetViewport,
    SetScissor,
    BindIndexBuffer,
    Draw,
    DrawIndexed,
    DrawIndexedIndirectCount,
    CopyBuffer,
    CopyBufferToTexture,
    CopyTextureToBuffer,
    CopyTexture,
    BlitTexture,
    FillBuffer,
    ClearTexture,
    Timestamp,
}

/// <summary>
/// One command, as plain fields a kind reads its own way: a resource's id and a second's, two byte offsets, a size,
/// four numbers and a block of constants. <see cref="GpuCommandRecorder"/> fills them; the GPU provider reads them.
/// </summary>
public readonly record struct GpuCommand
{
    public GpuCommand() { }

    public required GpuCommandKind Kind { get; init; }

    public uint Id { get; init; }

    public uint Id2 { get; init; }

    public ulong Offset { get; init; }

    public ulong Offset2 { get; init; }

    public ulong Size { get; init; }

    public uint X { get; init; }

    public uint Y { get; init; }

    public uint Z { get; init; }

    public uint W { get; init; }

    public GpuConstants Constants { get; init; }

    /// <summary>
    /// A timestamp's name, which <see cref="GpuTimings"/> reports it under.
    /// </summary>
    public string Label { get; init; } = string.Empty;
}
