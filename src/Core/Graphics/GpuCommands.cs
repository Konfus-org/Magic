using System.Collections.Immutable;
using System.Runtime.InteropServices;

namespace Magic.Graphics;

/// <summary>
/// A frame's commands as a value: passes in the order they run, each with what it uses. The gems record it into
/// Core's <see cref="RenderGraph"/> and Core hands it to <see cref="IGraphicsApi.Present"/>; the GPU provider
/// turns it into its API's calls and nothing else decides what the GPU does.
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
    /// Dispatches of <see cref="GpuPass.Shader"/>, with the constants they take.
    /// </summary>
    Compute,

    /// <summary>
    /// Draws with <see cref="GpuPass.DrawShaders"/> into <see cref="GpuPass.Targets"/>, with the constants, scissors and
    /// index buffer they take.
    /// </summary>
    Draw,

    /// <summary>
    /// Copies, blits and fills.
    /// </summary>
    Transfer,
}

/// <summary>
/// One pass: a debug group the GPU provider times, what it uses, which the provider puts the barriers in for before
/// anything in it runs, and its commands.
/// </summary>
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
    /// The compute shader the GPU provider binds before the commands, built from the entry; the pass is left out of a
    /// frame while the shader is not built.
    /// </summary>
    public ShaderEntry? Shader { get; init; }

    /// <summary>
    /// What a draw pass draws with, which the GPU provider binds before the commands as <see cref="Shader"/> is bound.
    /// </summary>
    public DrawShaders? DrawShaders { get; init; }
}

/// <summary>
/// The frame's stages: every pass of a stage runs after every pass of the stage before, whichever gem recorded it
/// first, and within a stage in the order they were recorded.
/// </summary>
public enum RenderStage : byte
{
    /// <summary>
    /// What the frame's CPU data goes up as: uploads, clears and acceleration structures, and what is worked out from
    /// them before anything draws, such as the sky's tables.
    /// </summary>
    Prepare,

    /// <summary>
    /// What the view sees: depth, the GBuffer and the glass targets.
    /// </summary>
    Geometry,

    /// <summary>
    /// What lights the view's surfaces: each tile's lights, shadows and indirect light.
    /// </summary>
    Light,

    /// <summary>
    /// The view's surfaces shaded into the picture.
    /// </summary>
    Shade,

    /// <summary>
    /// What finishes the picture, and its copy to the window or a screenshot.
    /// </summary>
    Post,

    /// <summary>
    /// What draws over the finished picture, as a debug UI does.
    /// </summary>
    Overlay,
}

/// <summary>
/// A resource a pass touches and how. The GPU provider knows the access it was left in, by the pass before or the
/// frame before, and puts in the barrier between the two; a read after the same read needs none. A texture's covers
/// one level, or every level when <see cref="Level"/> is -1.
/// </summary>
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
/// One colour or depth target of a draw pass: a texture's level, and what to do with what it holds. A clear is to
/// zero, which is black, or infinitely far as depth is reversed.
/// </summary>
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
/// The 16 bytes a pass pushes to its shaders, read there as <c>Core/Pass.slang</c>'s header with the same four names:
/// what a small pass needs, or in <see cref="X"/> the heap id of the buffer its constants struct is in.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct GpuConstants(uint X, uint Y = 0, uint Z = 0, uint W = 0);

/// <summary>
/// Which command a <see cref="GpuCommand"/> is; the fields each reads are on the method of <see cref="RenderGraph"/>,
/// <see cref="ComputePass"/> or <see cref="DrawPass"/> that records it.
/// </summary>
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
/// One command, as plain fields a kind reads its own way: a resource's id and a second's, two byte offsets, a size,
/// four numbers and a block of constants. The graph and its passes fill them; the GPU provider reads them.
/// </summary>
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
