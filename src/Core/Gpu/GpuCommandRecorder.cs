using System.Collections.Immutable;

namespace Magic.Gpu;

/// <summary>
/// Builds a <see cref="GpuCommands"/>: begin a pass, add its transitions and commands, end it, and take the value with
/// <see cref="Finish"/>. One per frame, on whatever thread records; the value it makes is handed to
/// <see cref="IGraphicsApi.Submit"/> on the render thread. Each method's parameters say which of a
/// <see cref="GpuCommand"/>'s fields its kind reads.
/// </summary>
public sealed class GpuCommandRecorder
{
    private readonly ImmutableArray<GpuPass>.Builder _passes = ImmutableArray.CreateBuilder<GpuPass>();
    private readonly ImmutableArray<GpuTransition>.Builder _transitions = ImmutableArray.CreateBuilder<GpuTransition>();
    private readonly ImmutableArray<GpuCommand>.Builder _commands = ImmutableArray.CreateBuilder<GpuCommand>();

    private GpuPass? _pass;

    public void BeginComputePass(string name) => Begin(new() { Name = name, Kind = GpuPassKind.Compute });

    public void BeginDrawPass(string name, in GpuRenderTargets targets) =>
        Begin(
            new()
            {
                Name = name,
                Kind = GpuPassKind.Draw,
                Targets = targets,
            }
        );

    public void BeginTransferPass(string name) => Begin(new() { Name = name, Kind = GpuPassKind.Transfer });

    public void EndPass()
    {
        GpuPass pass = _pass ?? throw new InvalidOperationException("No pass has begun.");
        _passes.Add(
            pass with
            {
                Transitions = _transitions.DrainToImmutable(),
                Commands = _commands.DrainToImmutable(),
            }
        );
        _pass = null;
    }

    /// <summary>
    /// The passes recorded, and the recorder empty for the next frame.
    /// </summary>
    public GpuCommands Finish()
    {
        if (_pass is { } open)
            throw new InvalidOperationException($"The pass {open.Name} was not ended.");

        return new(_passes.DrainToImmutable());
    }

    public void Transition(GpuBuffer buffer, GpuAccess before, GpuAccess after)
    {
        ThrowIfNotInPass();
        _transitions.Add(
            new()
            {
                Id = buffer.Id,
                IsTexture = false,
                Before = before,
                After = after,
            }
        );
    }

    /// <summary>
    /// Every level of <paramref name="texture"/>, or <paramref name="level"/> alone.
    /// </summary>
    public void Transition(GpuTexture texture, GpuAccess before, GpuAccess after, int level = -1)
    {
        ThrowIfNotInPass();
        _transitions.Add(
            new()
            {
                Id = texture.Id,
                IsTexture = true,
                Before = before,
                After = after,
                Level = level,
            }
        );
    }

    public void BindPipeline(GpuPipeline pipeline) =>
        Add(new() { Kind = GpuCommandKind.BindPipeline, Id = pipeline.Id });

    public void PushConstants(in GpuConstants constants) =>
        Add(new() { Kind = GpuCommandKind.PushConstants, Constants = constants });

    /// <summary>
    /// Builds an acceleration structure: a bottom-level one over its triangles, a top-level one over the first
    /// <paramref name="instanceCount"/> instances of its instance buffer. The build waits for every command before
    /// it and every command after waits for the build.
    /// </summary>
    public void BuildAcceleration(GpuAcceleration acceleration, uint instanceCount = 0) =>
        Add(
            new()
            {
                Kind = GpuCommandKind.BuildAcceleration,
                Id = acceleration.Id,
                X = instanceCount,
            }
        );

    public void Dispatch(uint groupsX, uint groupsY = 1, uint groupsZ = 1) =>
        Add(
            new()
            {
                Kind = GpuCommandKind.Dispatch,
                X = groupsX,
                Y = groupsY,
                Z = groupsZ,
            }
        );

    /// <summary>
    /// Three unsigned group counts read from <paramref name="arguments"/> at <paramref name="offset"/>.
    /// </summary>
    public void DispatchIndirect(GpuBuffer arguments, ulong offset) =>
        Add(
            new()
            {
                Kind = GpuCommandKind.DispatchIndirect,
                Id = arguments.Id,
                Offset = offset,
            }
        );

    /// <summary>
    /// The whole target when every argument is 0.
    /// </summary>
    public void SetScissor(uint x, uint y, uint width, uint height) =>
        Add(
            new()
            {
                Kind = GpuCommandKind.SetScissor,
                X = x,
                Y = y,
                Z = width,
                W = height,
            }
        );

    /// <summary>
    /// 32-bit indices from <paramref name="offset"/> on.
    /// </summary>
    public void BindIndexBuffer(GpuBuffer indices, ulong offset = 0) =>
        Add(
            new()
            {
                Kind = GpuCommandKind.BindIndexBuffer,
                Id = indices.Id,
                Offset = offset,
            }
        );

    public void DrawIndexed(uint indices, uint instances = 1, uint firstIndex = 0, uint firstInstance = 0) =>
        Add(
            new()
            {
                Kind = GpuCommandKind.DrawIndexed,
                X = indices,
                Y = instances,
                Z = firstIndex,
                W = firstInstance,
            }
        );

    /// <summary>
    /// As many draws as the unsigned count at <paramref name="countOffset"/> in <paramref name="count"/>, up to
    /// <paramref name="maxDraws"/>, their arguments <paramref name="stride"/> bytes apart from
    /// <paramref name="argumentsOffset"/> in <paramref name="arguments"/>, each four unsigned integers: vertices,
    /// instances, first vertex, first instance.
    /// </summary>
    public void DrawIndirectCount(
        GpuBuffer arguments,
        ulong argumentsOffset,
        GpuBuffer count,
        ulong countOffset,
        uint maxDraws,
        uint stride = 16
    ) =>
        Add(
            new()
            {
                Kind = GpuCommandKind.DrawIndirectCount,
                Id = arguments.Id,
                Offset = argumentsOffset,
                Id2 = count.Id,
                Offset2 = countOffset,
                X = maxDraws,
                Y = stride,
            }
        );

    public void CopyBuffer(
        GpuBuffer source,
        ulong sourceOffset,
        GpuBuffer destination,
        ulong destinationOffset,
        ulong bytes
    ) =>
        Add(
            new()
            {
                Kind = GpuCommandKind.CopyBuffer,
                Id = source.Id,
                Offset = sourceOffset,
                Id2 = destination.Id,
                Offset2 = destinationOffset,
                Size = bytes,
            }
        );

    /// <summary>
    /// A whole level of a layer, tightly packed from <paramref name="sourceOffset"/> on.
    /// </summary>
    public void CopyBufferToTexture(
        GpuBuffer source,
        ulong sourceOffset,
        GpuTexture destination,
        int level = 0,
        int layer = 0
    ) =>
        Add(
            new()
            {
                Kind = GpuCommandKind.CopyBufferToTexture,
                Id = source.Id,
                Offset = sourceOffset,
                Id2 = destination.Id,
                X = (uint)level,
                Y = (uint)layer,
            }
        );

    /// <summary>
    /// A whole level of a layer, tightly packed from <paramref name="destinationOffset"/> on.
    /// </summary>
    public void CopyTextureToBuffer(
        GpuTexture source,
        int level,
        int layer,
        GpuBuffer destination,
        ulong destinationOffset
    ) =>
        Add(
            new()
            {
                Kind = GpuCommandKind.CopyTextureToBuffer,
                Id = source.Id,
                X = (uint)level,
                Y = (uint)layer,
                Id2 = destination.Id,
                Offset2 = destinationOffset,
            }
        );

    /// <summary>
    /// Level 0 of one texture stretched over level 0 of another, filtered linearly; the formats may differ.
    /// </summary>
    public void BlitTexture(GpuTexture source, GpuTexture destination) =>
        Add(
            new()
            {
                Kind = GpuCommandKind.BlitTexture,
                Id = source.Id,
                Id2 = destination.Id,
            }
        );

    /// <summary>
    /// Every 4 bytes from <paramref name="offset"/> for <paramref name="bytes"/>, or to the end when 0, set to
    /// <paramref name="value"/>.
    /// </summary>
    public void FillBuffer(GpuBuffer buffer, uint value, ulong offset = 0, ulong bytes = 0) =>
        Add(
            new()
            {
                Kind = GpuCommandKind.FillBuffer,
                Id = buffer.Id,
                Offset = offset,
                Size = bytes,
                X = value,
            }
        );

    private void Begin(in GpuPass pass)
    {
        if (_pass is { } open)
            throw new InvalidOperationException($"The pass {open.Name} was not ended before {pass.Name} began.");

        _pass = pass;
    }

    private void Add(in GpuCommand command)
    {
        ThrowIfNotInPass();
        _commands.Add(command);
    }

    private void ThrowIfNotInPass()
    {
        if (_pass is null)
            throw new InvalidOperationException("No pass has begun.");
    }
}
