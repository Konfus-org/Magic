using System.Runtime.InteropServices;
using System.Text;
using Magic.Contexts.Assets;
using Magic.Interfaces;
using Rectangle = System.Drawing.Rectangle;

namespace Magic.Contexts.Gpu;

/// <summary>
/// A frame's GPU commands, in order, with the handles and bytes they point into: plain data, which the
/// <see cref="IGpuDriver"/> provider turns into its API's calls when it submits the frame, and then clears. The render
/// thread's, as <see cref="IGpuDriver"/> is. A gem records into <see cref="IGpuDriver.Commands"/> in its Render hook,
/// so what a gem made later records goes on top.
/// <para>
/// Drawing and computing happen inside a pass: <see cref="RenderPass"/> and <see cref="ComputePass()"/> each hand back
/// the commands that are legal inside it, which chain, and end the pass when disposed, so a pass is a
/// <c>using</c> block. Between passes a frame copies, makes mipmaps and names stretches of itself for a GPU debugger
/// (<see cref="DebugGroup"/>, a <c>using</c> block too).
/// </para>
/// </summary>
public sealed class GpuCommands
{
    private readonly List<GpuCommand> _commands = [];
    private readonly List<GpuTextureLevel> _levels = [];
    private readonly List<GpuBuffer> _buffers = [];
    private readonly List<GpuResource> _resources = [];
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
    /// The run of buffers <paramref name="command"/> points at.
    /// </summary>
    public ReadOnlySpan<GpuBuffer> GetBuffers(in GpuCommand command)
        => CollectionsMarshal.AsSpan(_buffers).Slice(command.RunStart, command.RunLength);

    /// <summary>
    /// The run of resources <paramref name="command"/> points at.
    /// </summary>
    public ReadOnlySpan<GpuResource> GetResources(in GpuCommand command)
        => CollectionsMarshal.AsSpan(_resources).Slice(command.RunStart, command.RunLength);

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
        _buffers.Clear();
        _resources.Clear();
        _regions.Clear();
        _bytes.Clear();
    }

    /// <summary>
    /// Starts a group a GPU debugger (RenderDoc, PIX, Xcode) shows the commands after it under, named
    /// <paramref name="name"/>, until the group is disposed; outside any pass. Allocates nothing.
    /// </summary>
    public GpuDebugGroup DebugGroup(string name)
    {
        // The name's UTF-8 written straight into the frame's bytes, which the command points at.
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
    /// Starts a compute pass, which says what its shaders read and write with <see cref="GpuComputePass.Reads"/> and
    /// <see cref="GpuComputePass.Writes"/>. The pass ends when what is handed back is disposed.
    /// </summary>
    public GpuComputePass ComputePass()
    {
        _commands.Add(new GpuCommand(GpuCommandType.BeginComputePass));

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

    internal void IndexBuffer(GpuBuffer buffer, GpuIndexSize size)
        => _commands.Add(new GpuCommand(GpuCommandType.IndexBuffer, Buffer: buffer, IndexSize: size));

    internal void Constants<T>(in T constants)
        where T : unmanaged => Constants(MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in constants)));

    internal void Constants(ReadOnlySpan<byte> constants)
    {
        int start = _bytes.Count;
        _bytes.AddRange(constants);
        _commands.Add(new GpuCommand(GpuCommandType.Constants, RunStart: start, RunLength: constants.Length));
    }

    internal void Reads(ReadOnlySpan<GpuResource> resources) => Use(GpuCommandType.Reads, resources);

    internal void Writes(ReadOnlySpan<GpuResource> resources) => Use(GpuCommandType.Writes, resources);

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

    private void Use(GpuCommandType type, ReadOnlySpan<GpuResource> resources)
    {
        int start = _resources.Count;
        _resources.AddRange(resources);
        _commands.Add(new GpuCommand(type, RunStart: start, RunLength: resources.Length));
    }
}
