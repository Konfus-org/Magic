using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Magic.Contexts.Assets;
using Magic.Interfaces;
using Rectangle = System.Drawing.Rectangle;

namespace Magic.Contexts.Gpu;

/// <summary>
/// A frame's GPU commands, in order, with the handles and bytes they point into: plain data, which the
/// <see cref="IGpu"/> provider turns into its API's calls when it submits the frame, and then clears. The render
/// thread's, as <see cref="IGpu"/> is. A gem records into <see cref="IGpu.Commands"/> in its Render hook, so what a
/// gem made later records goes on top.
/// <para>
/// Drawing and computing happen inside a pass: <see cref="RenderPass"/> and <see cref="ComputePass"/> each hand back
/// the commands that are legal inside it, which chain, and end the pass when disposed, so a pass is a
/// <c>using</c> block. Between passes a frame copies, makes mipmaps and names stretches of itself for a GPU debugger
/// (<see cref="DebugGroup"/>, a <c>using</c> block too).
/// </para>
/// </summary>
public sealed class GpuCommands
{
    private readonly List<GpuCommand> _commands = [];
    private readonly List<GpuTextureLevel> _levels = [];
    private readonly List<GpuSampledTexture> _sampled = [];
    private readonly List<GpuTexture> _textures = [];
    private readonly List<GpuBuffer> _buffers = [];
    private readonly List<GpuTextureRegion> _regions = [];
    private readonly List<byte> _bytes = [];

    public ReadOnlySpan<GpuCommand> Commands => CollectionsMarshal.AsSpan(_commands);

    public int Count => _commands.Count;

    /// <summary>
    /// The run of texture levels <paramref name="command"/> points at.
    /// </summary>
    public ReadOnlySpan<GpuTextureLevel> GetLevels(in GpuCommand command)
        => CollectionsMarshal.AsSpan(_levels).Slice(command.RunStart, command.RunLength);

    /// <summary>
    /// The run of sampled textures <paramref name="command"/> points at.
    /// </summary>
    public ReadOnlySpan<GpuSampledTexture> GetSampled(in GpuCommand command)
        => CollectionsMarshal.AsSpan(_sampled).Slice(command.RunStart, command.RunLength);

    /// <summary>
    /// The run of textures <paramref name="command"/> points at.
    /// </summary>
    public ReadOnlySpan<GpuTexture> GetTextures(in GpuCommand command)
        => CollectionsMarshal.AsSpan(_textures).Slice(command.RunStart, command.RunLength);

    /// <summary>
    /// The run of buffers <paramref name="command"/> points at: its second run for a compute pass, whose first is its
    /// levels.
    /// </summary>
    public ReadOnlySpan<GpuBuffer> GetBuffers(in GpuCommand command) => command.Type == GpuCommandType.BeginComputePass
        ? CollectionsMarshal.AsSpan(_buffers).Slice(command.SecondRunStart, command.SecondRunLength)
        : CollectionsMarshal.AsSpan(_buffers).Slice(command.RunStart, command.RunLength);

    /// <summary>
    /// The run of texture regions <paramref name="command"/> points at.
    /// </summary>
    public ReadOnlySpan<GpuTextureRegion> GetRegions(in GpuCommand command)
        => CollectionsMarshal.AsSpan(_regions).Slice(command.RunStart, command.RunLength);

    /// <summary>
    /// The run of bytes <paramref name="command"/> points at.
    /// </summary>
    public ReadOnlySpan<byte> GetBytes(in GpuCommand command)
        => CollectionsMarshal.AsSpan(_bytes).Slice(command.RunStart, command.RunLength);

    public void Clear()
    {
        _commands.Clear();
        _levels.Clear();
        _sampled.Clear();
        _textures.Clear();
        _buffers.Clear();
        _regions.Clear();
        _bytes.Clear();
    }

    /// <summary>
    /// Starts a group a GPU debugger (RenderDoc, PIX, Xcode) shows the commands after it under, named
    /// <paramref name="name"/>, until the group is disposed; outside any pass. Allocates nothing.
    /// </summary>
    public GpuDebugGroup DebugGroup(string name)
    {
        int start = _bytes.Count;
        int length = Encoding.UTF8.GetByteCount(name);
        CollectionsMarshal.SetCount(_bytes, start + length);
        Encoding.UTF8.GetBytes(name, CollectionsMarshal.AsSpan(_bytes)[start..]);
        _commands.Add(new GpuCommand(GpuCommandType.BeginDebugGroup, RunStart: start, RunLength: length));

        return new GpuDebugGroup(this);
    }

    /// <summary>
    /// Starts a render pass into <paramref name="colors"/> (none for a depth-only pass) and <paramref name="depth"/>
    /// unless its texture is <see cref="GpuTexture.None"/>, each one level of one layer. <paramref name="contents"/>
    /// goes for every target: a clear is to <paramref name="clear"/>, and depth to 0, reverse-Z's far. The pass ends
    /// when what is handed back is disposed.
    /// </summary>
    public GpuRenderPass RenderPass(
        ReadOnlySpan<GpuTextureLevel> colors,
        GpuContents contents,
        Color clear = default,
        GpuTextureLevel depth = default)
    {
        int start = _levels.Count;
        _levels.AddRange(colors);
        _commands.Add(new GpuCommand(
            GpuCommandType.BeginRenderPass,
            Depth: depth,
            Contents: contents,
            Clear: clear,
            RunStart: start,
            RunLength: colors.Length));

        return new GpuRenderPass(this);
    }

    /// <summary>
    /// Starts a compute pass that writes <paramref name="textures"/>, each one level of one layer, and
    /// <paramref name="buffers"/>. The pass ends when what is handed back is disposed.
    /// </summary>
    public GpuComputePass ComputePass(ReadOnlySpan<GpuTextureLevel> textures, ReadOnlySpan<GpuBuffer> buffers)
    {
        int levelStart = _levels.Count;
        _levels.AddRange(textures);
        int bufferStart = _buffers.Count;
        _buffers.AddRange(buffers);
        _commands.Add(new GpuCommand(
            GpuCommandType.BeginComputePass,
            RunStart: levelStart,
            RunLength: textures.Length,
            SecondRunStart: bufferStart,
            SecondRunLength: buffers.Length));

        return new GpuComputePass(this);
    }

    /// <summary>
    /// Copies the texels of <paramref name="from"/> (its size, the whole level where it says 0) into
    /// <paramref name="to"/>, from its corner, between textures of the same format; outside any pass.
    /// </summary>
    public GpuCommands CopyTexture(in GpuTextureRegion from, in GpuTextureRegion to)
    {
        int start = _regions.Count;
        _regions.Add(from);
        _regions.Add(to);
        _commands.Add(new GpuCommand(GpuCommandType.CopyTexture, RunStart: start, RunLength: 2));

        return this;
    }

    /// <summary>
    /// Copies <paramref name="bytes"/> from <paramref name="from"/> at <paramref name="fromOffset"/> to
    /// <paramref name="to"/> at <paramref name="toOffset"/>; outside any pass.
    /// </summary>
    public GpuCommands CopyBuffer(GpuBuffer from, uint fromOffset, GpuBuffer to, uint toOffset, uint bytes)
    {
        int start = _buffers.Count;
        _buffers.Add(from);
        _buffers.Add(to);
        _commands.Add(new GpuCommand(
            GpuCommandType.CopyBuffer,
            RunStart: start,
            RunLength: 2,
            Count: bytes,
            First: fromOffset,
            Offset: toOffset));

        return this;
    }

    /// <summary>
    /// Makes every level of <paramref name="texture"/> past the first from the one before, each layer alone; outside
    /// any pass. The texture is sampled and drawn into (<see cref="GpuTextureUsage.Sampled"/> and
    /// <see cref="GpuTextureUsage.ColorTarget"/>).
    /// </summary>
    public GpuCommands GenerateMipmaps(GpuTexture texture)
    {
        _commands.Add(new GpuCommand(GpuCommandType.GenerateMipmaps, Texture: texture));

        return this;
    }

    internal void EndDebugGroup() => _commands.Add(new GpuCommand(GpuCommandType.EndDebugGroup));

    internal void EndRenderPass() => _commands.Add(new GpuCommand(GpuCommandType.EndRenderPass));

    internal void EndComputePass() => _commands.Add(new GpuCommand(GpuCommandType.EndComputePass));

    internal void Viewport(Rectangle area) => _commands.Add(new GpuCommand(GpuCommandType.Viewport, Area: area));

    internal void Scissor(Rectangle area) => _commands.Add(new GpuCommand(GpuCommandType.Scissor, Area: area));

    internal void Pipeline(GpuPipeline pipeline)
        => _commands.Add(new GpuCommand(GpuCommandType.Pipeline, Pipeline: pipeline));

    internal void VertexBuffers(ReadOnlySpan<GpuBuffer> buffers)
    {
        int start = _buffers.Count;
        _buffers.AddRange(buffers);
        _commands.Add(new GpuCommand(GpuCommandType.VertexBuffers, RunStart: start, RunLength: buffers.Length));
    }

    internal void IndexBuffer(GpuBuffer buffer, GpuIndexSize size)
        => _commands.Add(new GpuCommand(GpuCommandType.IndexBuffer, Buffer: buffer, IndexSize: size));

    internal void Buffers(GpuStage stage, ReadOnlySpan<GpuBuffer> buffers)
    {
        int start = _buffers.Count;
        _buffers.AddRange(buffers);
        _commands.Add(new GpuCommand(GpuCommandType.Buffers, Stage: stage, RunStart: start, RunLength: buffers.Length));
    }

    internal void RawTextures(GpuStage stage, ReadOnlySpan<GpuTexture> textures)
    {
        int start = _textures.Count;
        _textures.AddRange(textures);
        _commands.Add(
            new GpuCommand(GpuCommandType.RawTextures, Stage: stage, RunStart: start, RunLength: textures.Length));
    }

    internal void Textures(GpuStage stage, ReadOnlySpan<GpuSampledTexture> textures)
    {
        int start = _sampled.Count;
        _sampled.AddRange(textures);
        _commands.Add(
            new GpuCommand(GpuCommandType.Textures, Stage: stage, RunStart: start, RunLength: textures.Length));
    }

    internal void Constants<T>(GpuStage stage, in T constants)
        where T : unmanaged
    {
        int start = _bytes.Count;
        _bytes.AddRange(MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in constants)));
        _commands.Add(
            new GpuCommand(GpuCommandType.Constants, Stage: stage, RunStart: start, RunLength: Unsafe.SizeOf<T>()));
    }

    internal void Draw(uint vertices, uint instances, uint firstVertex, uint firstInstance)
        => _commands.Add(new GpuCommand(
            GpuCommandType.Draw,
            Count: vertices,
            Instances: instances,
            First: firstVertex,
            FirstInstance: firstInstance));

    internal void DrawIndexed(uint indices, uint firstIndex, int vertexOffset, uint instances, uint firstInstance)
        => _commands.Add(new GpuCommand(
            GpuCommandType.DrawIndexed,
            Count: indices,
            Instances: instances,
            First: firstIndex,
            VertexOffset: vertexOffset,
            FirstInstance: firstInstance));

    internal void DrawIndexedFromBuffer(GpuBuffer arguments, uint offset, uint count)
        => _commands.Add(
            new GpuCommand(GpuCommandType.DrawIndexedFromBuffer, Buffer: arguments, Count: count, Offset: offset));

    internal void Dispatch(uint groupsX, uint groupsY, uint groupsZ)
        => _commands.Add(new GpuCommand(GpuCommandType.Dispatch, GroupsX: groupsX, GroupsY: groupsY, GroupsZ: groupsZ));

    internal void DispatchFromBuffer(GpuBuffer arguments, uint offset)
        => _commands.Add(new GpuCommand(GpuCommandType.DispatchFromBuffer, Buffer: arguments, Offset: offset));
}
