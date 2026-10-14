using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Magic.Contexts.Assets;
using Magic.Interfaces;
using Rectangle = System.Drawing.Rectangle;

namespace Magic.Contexts.Gpu;

/// <summary>
/// A frame's GPU commands, in order, with the bindings and bytes they point into: plain data, which the
/// <see cref="IGpu"/> provider turns into its API's calls when it submits the frame, and then clears. One method per
/// command (<see cref="GpuCommandType"/>); the render thread's, as <see cref="IGpu"/> is. A gem records into
/// <see cref="IGpu.Commands"/> in its Render hook, so what a gem made later records goes on top.
/// </summary>
public sealed class GpuCommands
{
    private readonly List<GpuCommand> _commands = [];
    private readonly List<GpuBinding> _bindings = [];
    private readonly List<byte> _bytes = [];

    public ReadOnlySpan<GpuCommand> Commands => CollectionsMarshal.AsSpan(_commands);

    public int Count => _commands.Count;

    /// <summary>
    /// The run of bindings <paramref name="command"/> points at.
    /// </summary>
    public ReadOnlySpan<GpuBinding> BindingsOf(in GpuCommand command)
        => CollectionsMarshal.AsSpan(_bindings).Slice(command.RunStart, command.RunLength);

    /// <summary>
    /// The run of bytes <paramref name="command"/> points at.
    /// </summary>
    public ReadOnlySpan<byte> BytesOf(in GpuCommand command)
        => CollectionsMarshal.AsSpan(_bytes).Slice(command.RunStart, command.RunLength);

    public void Clear()
    {
        _commands.Clear();
        _bindings.Clear();
        _bytes.Clear();
    }

    /// <summary>
    /// A render pass into <paramref name="colors"/>, each a texture at a mip level and layer (none for a depth-only
    /// pass), and <paramref name="depth"/> at <paramref name="depthLayer"/> unless it is
    /// <see cref="GpuTexture.None"/>. <paramref name="load"/> goes for every target: a clear is to
    /// <paramref name="clear"/>, and depth to 0, reverse-Z's far.
    /// </summary>
    public void BeginRenderPass(ReadOnlySpan<GpuBinding> colors, GpuLoad load, Color clear = default, GpuTexture depth = default, uint depthLayer = 0)
    {
        (int start, int length) = Add(colors);
        _commands.Add(new GpuCommand(GpuCommandType.BeginRenderPass, Depth: depth, DepthLayer: depthLayer, Load: load, Clear: clear, RunStart: start, RunLength: length));
    }

    public void EndRenderPass()
    {
        _commands.Add(new GpuCommand(GpuCommandType.EndRenderPass));
    }

    /// <summary>
    /// A compute pass that writes <paramref name="writes"/>: buffers, and textures as storage at a mip level and layer.
    /// </summary>
    public void BeginComputePass(ReadOnlySpan<GpuBinding> writes)
    {
        (int start, int length) = Add(writes);
        _commands.Add(new GpuCommand(GpuCommandType.BeginComputePass, RunStart: start, RunLength: length));
    }

    public void EndComputePass()
    {
        _commands.Add(new GpuCommand(GpuCommandType.EndComputePass));
    }

    public void SetViewport(Rectangle area)
    {
        _commands.Add(new GpuCommand(GpuCommandType.SetViewport, Area: area));
    }

    public void SetScissor(Rectangle area)
    {
        _commands.Add(new GpuCommand(GpuCommandType.SetScissor, Area: area));
    }

    public void BindPipeline(GpuPipeline pipeline)
    {
        _commands.Add(new GpuCommand(GpuCommandType.BindPipeline, Pipeline: pipeline));
    }

    public void BindVertexBuffers(uint slot, ReadOnlySpan<GpuBuffer> buffers)
    {
        (int start, int length) = Add(buffers);
        _commands.Add(new GpuCommand(GpuCommandType.BindVertexBuffers, Slot: slot, RunStart: start, RunLength: length));
    }

    /// <summary>
    /// The index buffer, of 32-bit indices when <paramref name="wide"/>, else 16-bit.
    /// </summary>
    public void BindIndexBuffer(GpuBuffer buffer, bool wide)
    {
        _commands.Add(new GpuCommand(GpuCommandType.BindIndexBuffer, Buffer: buffer, Wide: wide));
    }

    /// <summary>
    /// Read-only storage buffers, from <paramref name="slot"/> on. Slots count a stage's storage buffers alone: the
    /// first is slot 0, whatever <c>READ(n)</c> it has after the stage's textures (Shaders/Include/Bindings.hlsli).
    /// </summary>
    public void BindStorageBuffers(GpuStage stage, uint slot, ReadOnlySpan<GpuBuffer> buffers)
    {
        (int start, int length) = Add(buffers);
        _commands.Add(new GpuCommand(GpuCommandType.BindStorageBuffers, Stage: stage, Slot: slot, RunStart: start, RunLength: length));
    }

    /// <summary>
    /// Read-only storage textures, from <paramref name="slot"/> on. Slots count a stage's storage textures alone: the
    /// first is slot 0, whatever <c>READ(n)</c> it has after the stage's sampled textures.
    /// </summary>
    public void BindStorageTextures(GpuStage stage, uint slot, ReadOnlySpan<GpuTexture> textures)
    {
        int start = _bindings.Count;
        foreach (GpuTexture texture in textures)
        {
            _bindings.Add(new GpuBinding(Texture: texture));
        }

        _commands.Add(new GpuCommand(GpuCommandType.BindStorageTextures, Stage: stage, Slot: slot, RunStart: start, RunLength: textures.Length));
    }

    /// <summary>
    /// Sampled textures, each with its sampler, from <paramref name="slot"/> on: slot n is <c>READ(n)</c> with
    /// <c>SAMPLER(n)</c>.
    /// </summary>
    public void BindTextures(GpuStage stage, uint slot, ReadOnlySpan<GpuBinding> textures)
    {
        (int start, int length) = Add(textures);
        _commands.Add(new GpuCommand(GpuCommandType.BindTextures, Stage: stage, Slot: slot, RunStart: start, RunLength: length));
    }

    /// <summary>
    /// The stage's one constant block (<c>UNIFORM(0)</c>), copied now; a struct laid out as the <c>cbuffer</c> is, in
    /// whole 16-byte rows.
    /// </summary>
    public void Push<T>(GpuStage stage, in T constants)
        where T : unmanaged
    {
        int start = _bytes.Count;
        _bytes.AddRange(MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in constants)));
        _commands.Add(new GpuCommand(GpuCommandType.PushConstants, Stage: stage, RunStart: start, RunLength: Unsafe.SizeOf<T>()));
    }

    public void Draw(uint vertices, uint instances = 1, uint firstVertex = 0, uint firstInstance = 0)
    {
        _commands.Add(new GpuCommand(GpuCommandType.Draw, Count: vertices, Instances: instances, First: firstVertex, FirstInstance: firstInstance));
    }

    public void DrawIndexed(uint indices, uint firstIndex = 0, int vertexOffset = 0, uint instances = 1, uint firstInstance = 0)
    {
        _commands.Add(new GpuCommand(GpuCommandType.DrawIndexed, Count: indices, First: firstIndex, VertexOffset: vertexOffset, Instances: instances, FirstInstance: firstInstance));
    }

    /// <summary>
    /// <paramref name="count"/> indexed draws, their arguments read from <paramref name="arguments"/> at
    /// <paramref name="offset"/>, 20 bytes each: indices, instances, first index, vertex offset, first instance.
    /// </summary>
    public void DrawIndexedIndirect(GpuBuffer arguments, uint offset, uint count)
    {
        _commands.Add(new GpuCommand(GpuCommandType.DrawIndexedIndirect, Buffer: arguments, Offset: offset, Count: count));
    }

    public void Dispatch(uint groupsX, uint groupsY = 1, uint groupsZ = 1)
    {
        _commands.Add(new GpuCommand(GpuCommandType.Dispatch, GroupsX: groupsX, GroupsY: groupsY, GroupsZ: groupsZ));
    }

    /// <summary>
    /// The groups read from <paramref name="arguments"/> at <paramref name="offset"/>: three <c>uint</c>s.
    /// </summary>
    public void DispatchIndirect(GpuBuffer arguments, uint offset = 0)
    {
        _commands.Add(new GpuCommand(GpuCommandType.DispatchIndirect, Buffer: arguments, Offset: offset));
    }

    /// <summary>
    /// Copies <paramref name="width"/> by <paramref name="height"/> texels from the corner of one level and layer of a
    /// texture into another's, of the same format; outside any pass.
    /// </summary>
    public void CopyTexture(GpuBinding source, GpuBinding destination, uint width, uint height)
    {
        int start = _bindings.Count;
        _bindings.Add(source);
        _bindings.Add(destination);
        _commands.Add(new GpuCommand(GpuCommandType.CopyTexture, Area: new Rectangle(0, 0, (int)width, (int)height), RunStart: start, RunLength: 2));
    }

    /// <summary>
    /// Copies <paramref name="bytes"/> from one buffer to another; outside any pass.
    /// </summary>
    public void CopyBuffer(GpuBuffer source, uint sourceOffset, GpuBuffer destination, uint destinationOffset, uint bytes)
    {
        int start = _bindings.Count;
        _bindings.Add(new GpuBinding(source));
        _bindings.Add(new GpuBinding(destination));
        _commands.Add(new GpuCommand(GpuCommandType.CopyBuffer, First: sourceOffset, Offset: destinationOffset, Count: bytes, RunStart: start, RunLength: 2));
    }

    /// <summary>
    /// Draws a whole level of a layer of one texture into another's, scaled to fit and filtered; outside any pass. The
    /// destination may be a window's target, the source never.
    /// </summary>
    public void Blit(GpuBinding source, GpuBinding destination, GpuFilter filter)
    {
        int start = _bindings.Count;
        _bindings.Add(source);
        _bindings.Add(destination);
        _commands.Add(new GpuCommand(GpuCommandType.Blit, Slot: (uint)filter, RunStart: start, RunLength: 2));
    }

    /// <summary>
    /// Makes every level of <paramref name="texture"/> past the first from the one before, each layer alone; outside any
    /// pass. The texture is sampled and drawn into (<see cref="GpuTextureUsage.Sampler"/> and
    /// <see cref="GpuTextureUsage.ColorTarget"/>).
    /// </summary>
    public void GenerateMipmaps(GpuTexture texture)
    {
        int start = _bindings.Count;
        _bindings.Add(new GpuBinding(Texture: texture));
        _commands.Add(new GpuCommand(GpuCommandType.GenerateMipmaps, RunStart: start, RunLength: 1));
    }

    /// <summary>
    /// Starts a group a GPU debugger (RenderDoc, PIX, Xcode) shows the commands after it under, named
    /// <paramref name="name"/>, until <see cref="PopDebugGroup"/>; outside any pass. Allocates nothing.
    /// </summary>
    public void PushDebugGroup(string name)
    {
        int start = _bytes.Count;
        int length = Encoding.UTF8.GetByteCount(name);
        CollectionsMarshal.SetCount(_bytes, start + length);
        Encoding.UTF8.GetBytes(name, CollectionsMarshal.AsSpan(_bytes)[start..]);
        _commands.Add(new GpuCommand(GpuCommandType.PushDebugGroup, RunStart: start, RunLength: length));
    }

    public void PopDebugGroup()
    {
        _commands.Add(new GpuCommand(GpuCommandType.PopDebugGroup));
    }

    private (int Start, int Length) Add(ReadOnlySpan<GpuBinding> bindings)
    {
        int start = _bindings.Count;
        _bindings.AddRange(bindings);
        return (start, bindings.Length);
    }

    private (int Start, int Length) Add(ReadOnlySpan<GpuBuffer> buffers)
    {
        int start = _bindings.Count;
        foreach (GpuBuffer buffer in buffers)
        {
            _bindings.Add(new GpuBinding(buffer));
        }

        return (start, buffers.Length);
    }
}
