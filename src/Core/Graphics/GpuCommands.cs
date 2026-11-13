using System.Collections.Immutable;
using System.Runtime.InteropServices;

namespace Magic.Graphics;

/// <summary>
/// A frame's commands as a value: passes in the order they run, each with what it uses.
/// </summary>
/// <remarks>
/// Gems record it into <see cref="RenderGraph"/> and Core hands it to <see cref="IGraphicsApi.Present"/>. Only the GPU
/// provider turns it into API calls.
/// </remarks>
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
    /// Dispatches of <see cref="GpuPass.Shader"/>, with the constants they take.
    /// </summary>
    Compute,

    /// <summary>
    /// Draws with <see cref="GpuPass.DrawShaders"/> into <see cref="GpuPass.Targets"/>, with the constants, scissors
    /// and index buffer they take.
    /// </summary>
    Draw,

    /// <summary>
    /// Copies, blits and fills.
    /// </summary>
    Transfer,
}

/// <summary>
/// One pass: a timed debug group with the resources it uses and its commands.
/// </summary>
/// <remarks>
/// The GPU provider puts in the barriers for its uses before any of its commands runs.
/// </remarks>
public readonly record struct GpuPass
{
    public GpuPass() { }

    public required string Name { get; init; }

    /// <summary>
    /// What the pass does, from the shaders it binds: none for a transfer pass.
    /// </summary>
    public GpuPassKind Kind =>
        DrawShaders is not null ? GpuPassKind.Draw
        : Shader is not null ? GpuPassKind.Compute
        : GpuPassKind.Transfer;

    public RenderStage Stage { get; init; }

    public ImmutableArray<GpuUse> Uses { get; init; } = [];

    public ImmutableArray<GpuCommand> Commands { get; init; } = [];

    /// <summary>
    /// What a <see cref="GpuPassKind.Draw"/> pass draws into.
    /// </summary>
    public GpuRenderTargets Targets { get; init; }

    /// <summary>
    /// The compute shader bound before the commands. The frame leaves the pass out while the shader is not built.
    /// </summary>
    public ShaderEntry? Shader { get; init; }

    /// <summary>
    /// The shaders of a draw pass, bound before the commands.
    /// </summary>
    public DrawShaders? DrawShaders { get; init; }
}

/// <summary>
/// Where a pass runs in the frame: passes of a lower <see cref="Order"/> run first.
/// </summary>
/// <remarks>
/// Passes of one order run in the order they were recorded, whichever gem records them. Core names the first and the
/// last stage; a renderer names the stages between them.
/// </remarks>
public readonly record struct RenderStage(byte Order)
{
    /// <summary>
    /// The first stage: uploads, clears, acceleration structures, and the work on them that must come before any draw.
    /// </summary>
    public static readonly RenderStage Prepare = new(0);

    /// <summary>
    /// The last stage: draws over the finished picture, as a debug UI does.
    /// </summary>
    public static readonly RenderStage Overlay = new(byte.MaxValue);
}

/// <summary>
/// A resource a pass touches and how.
/// </summary>
/// <remarks>
/// The GPU provider puts in the barrier from the access before, which can be in the frame before. A read after the
/// same read needs none. A texture's use covers one level, or every level when <see cref="Level"/> is -1.
/// </remarks>
public readonly record struct GpuUse
{
    public GpuUse() { }

    /// <summary>
    /// The buffer's or texture's id, as <see cref="IsTexture"/> says.
    /// </summary>
    public required uint Id { get; init; }

    public required bool IsTexture { get; init; }

    public required GpuAccess Access { get; init; }

    public int Level { get; init; } = -1;
}

/// <summary>
/// A buffer or a texture, as a pass names what it reads or writes.
/// </summary>
public readonly record struct GpuResource(uint Id, bool IsTexture)
{
    public static implicit operator GpuResource(GpuBuffer buffer) => new(buffer.Id, false);

    public static implicit operator GpuResource(GpuTexture texture) => new(texture.Id, true);
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
/// One colour or depth target of a draw pass: a texture's level, and what to do with what it holds.
/// </summary>
/// <remarks>
/// A clear writes zero: black for colour, infinitely far for depth, as depth is reversed.
/// </remarks>
public readonly record struct GpuTarget
{
    public GpuTarget() { }

    public required GpuTexture Texture { get; init; }

    public int Level { get; init; }

    public TargetLoad Load { get; init; } = TargetLoad.Keep;
}

/// <summary>
/// What a draw pass draws into: up to 8 colour targets of one size, and a depth target or none.
/// </summary>
public readonly record struct GpuRenderTargets
{
    public GpuRenderTargets() { }

    public ImmutableArray<GpuTarget> Colors { get; init; } = [];

    public GpuTarget? Depth { get; init; }
}

/// <summary>
/// The 16 bytes a pass pushes to its shaders: a small pass's values, or in <see cref="X"/> the heap id of its
/// constants.
/// </summary>
/// <remarks>
/// Shaders read them as the header of <c>Core/Pass.slang</c>, with the same four names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct GpuConstants(uint X, uint Y = 0, uint Z = 0, uint W = 0);

/// <summary>
/// Which command a <see cref="GpuCommand"/> is.
/// </summary>
/// <remarks>
/// The method of <see cref="RenderGraph"/>, <see cref="ComputePass"/> or <see cref="DrawPass"/> that records a kind
/// shows the fields it reads.
/// </remarks>
public enum GpuCommandKind : byte
{
    PushConstants,
    Dispatch,
    DispatchIndirect,
    SetScissor,
    BindIndexBuffer,
    DrawIndexed,
    DrawIndirectCount,
    CopyBuffer,
    CopyBufferToTexture,
    CopyTextureToBuffer,
    BlitTexture,
    FillBuffer,
    BuildAcceleration,
    DispatchThreads,
}

/// <summary>
/// One command, as plain fields that each <see cref="GpuCommandKind"/> reads its own way.
/// </summary>
/// <remarks>
/// The fields are two resource ids, two byte offsets, a size, four numbers and a block of constants.
/// </remarks>
public readonly record struct GpuCommand
{
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
}
