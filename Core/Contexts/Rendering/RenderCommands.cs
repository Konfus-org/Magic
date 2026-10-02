using System.Drawing;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Magic.Contexts.Rendering;

/// <summary>
/// One frame's GPU commands, in order, with the bindings and bytes they point into. The host's render system records
/// the scene into it, then any gem appends its own in its Render hook (so they draw on top), then the renderer runs it
/// with <see cref="Interfaces.IRendering.Submit"/> and it is cleared. One method per command; main thread only.
/// </summary>
public sealed class RenderCommands
{
    /// <summary>What an empty target is cleared to.</summary>
    public static readonly Vector4 ClearColor = new(0.02f, 0.03f, 0.08f, 1f);

    private readonly List<RenderCommand> _commands = [];
    private readonly List<GpuBinding> _bindings = [];
    private readonly List<byte> _bytes = [];

    public ReadOnlySpan<RenderCommand> Commands => CollectionsMarshal.AsSpan(_commands);

    public ReadOnlySpan<GpuBinding> Bindings => CollectionsMarshal.AsSpan(_bindings);

    public ReadOnlySpan<byte> Bytes => CollectionsMarshal.AsSpan(_bytes);

    public int Count => _commands.Count;

    /// <summary>Milliseconds the last submit of this list took; 0 before the first.</summary>
    public float SubmitMs { get; internal set; }

    /// <summary>How much of <see cref="SubmitMs"/> was spent blocked on the GPU.</summary>
    public float WaitMs { get; internal set; }

    /// <summary>The run a command points at.</summary>
    public ReadOnlySpan<GpuBinding> BindingsOf(in RenderCommand command)
    {
        return Bindings.Slice(command.Run.Start, command.Run.Length);
    }

    public ReadOnlySpan<byte> BytesOf(in RenderCommand command)
    {
        return Bytes.Slice(command.Run.Start, command.Run.Length);
    }

    public void Clear()
    {
        _commands.Clear();
        _bindings.Clear();
        _bytes.Clear();
    }

    /// <summary>A render pass into <paramref name="color"/> and, when valid, <paramref name="depth"/>, both loaded or cleared by <paramref name="load"/>.</summary>
    public void BeginRenderPass(GpuTexture color, GpuLoad load, GpuTexture depth = default)
    {
        _commands.Add(new RenderCommand(RenderCommandType.BeginRenderPass, Texture: color, Depth: depth, Load: load, ClearColor: ClearColor));
    }

    public void EndRenderPass()
    {
        _commands.Add(new RenderCommand(RenderCommandType.EndRenderPass));
    }

    /// <summary>A compute pass writing <paramref name="writes"/> (buffers, or textures as storage).</summary>
    public void BeginComputePass(ReadOnlySpan<GpuBinding> writes)
    {
        _commands.Add(new RenderCommand(RenderCommandType.BeginComputePass, Run: Add(writes)));
    }

    public void EndComputePass()
    {
        _commands.Add(new RenderCommand(RenderCommandType.EndComputePass));
    }

    public void SetViewport(Rectangle rect)
    {
        _commands.Add(new RenderCommand(RenderCommandType.SetViewport, Rect: rect));
    }

    public void SetScissor(Rectangle rect)
    {
        _commands.Add(new RenderCommand(RenderCommandType.SetScissor, Rect: rect));
    }

    public void BindPipeline(GpuPipeline pipeline)
    {
        _commands.Add(new RenderCommand(RenderCommandType.BindPipeline, Pipeline: pipeline));
    }

    public void BindVertexBuffers(uint slot, ReadOnlySpan<GpuBuffer> buffers)
    {
        int start = _bindings.Count;
        foreach (GpuBuffer buffer in buffers)
            _bindings.Add(new GpuBinding(buffer));

        _commands.Add(new RenderCommand(RenderCommandType.BindVertexBuffers, Slot: slot, Run: (start, buffers.Length)));
    }

    public void BindIndexBuffer(GpuBuffer buffer, bool wide)
    {
        _commands.Add(new RenderCommand(RenderCommandType.BindIndexBuffer, Buffer: buffer, Wide: wide));
    }

    public void BindStorageBuffers(GpuStage stage, uint slot, ReadOnlySpan<GpuBuffer> buffers)
    {
        int start = _bindings.Count;
        foreach (GpuBuffer buffer in buffers)
            _bindings.Add(new GpuBinding(buffer));

        _commands.Add(new RenderCommand(RenderCommandType.BindStorageBuffers, Stage: stage, Slot: slot, Run: (start, buffers.Length)));
    }

    /// <summary>Textures with their samplers (<see cref="GpuBinding.Texture"/> + <see cref="GpuBinding.Sampler"/>).</summary>
    public void BindTextures(GpuStage stage, uint slot, ReadOnlySpan<GpuBinding> textures)
    {
        _commands.Add(new RenderCommand(RenderCommandType.BindTextures, Stage: stage, Slot: slot, Run: Add(textures)));
    }

    /// <summary>The stage's constant block (binding 0), copied now.</summary>
    public void Push<T>(GpuStage stage, in T value) where T : unmanaged
    {
        int start = _bytes.Count;
        _bytes.AddRange(MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value)));
        _commands.Add(new RenderCommand(RenderCommandType.PushConstants, Stage: stage, Run: (start, Unsafe.SizeOf<T>())));
    }

    public void Draw(uint vertices, uint instances = 1, uint firstVertex = 0, uint firstInstance = 0)
    {
        _commands.Add(new RenderCommand(RenderCommandType.Draw, Count: vertices, Instances: instances, First: firstVertex, FirstInstance: firstInstance));
    }

    public void DrawIndexed(uint indices, uint firstIndex, int vertexOffset, uint instances = 1, uint firstInstance = 0)
    {
        _commands.Add(new RenderCommand(RenderCommandType.DrawIndexed, Count: indices, First: firstIndex, VertexOffset: vertexOffset, Instances: instances, FirstInstance: firstInstance));
    }

    public void DrawIndexedIndirect(GpuBuffer args, uint offset, uint count)
    {
        _commands.Add(new RenderCommand(RenderCommandType.DrawIndexedIndirect, Buffer: args, Offset: offset, Count: count));
    }

    public void Dispatch(uint x, uint y = 1, uint z = 1)
    {
        _commands.Add(new RenderCommand(RenderCommandType.Dispatch, Groups: (Math.Max(1, x), Math.Max(1, y), Math.Max(1, z))));
    }

    public void DispatchIndirect(GpuBuffer args, uint offset = 0)
    {
        _commands.Add(new RenderCommand(RenderCommandType.DispatchIndirect, Buffer: args, Offset: offset));
    }

    /// <summary>
    /// <paramref name="area"/> of <paramref name="source"/> stretched over <paramref name="destination"/>, one level of one
    /// layer; a destination of size 0 is its whole level (a window's swapchain image, whose size only the renderer knows).
    /// </summary>
    public void Blit(GpuTexture source, Rectangle area, in TextureRegion destination)
    {
        _commands.Add(new RenderCommand(RenderCommandType.Blit, Texture: source, Rect: area, Destination: destination));
    }

    private (int Start, int Length) Add(ReadOnlySpan<GpuBinding> bindings)
    {
        int start = _bindings.Count;
        _bindings.AddRange(bindings);
        return (start, bindings.Length);
    }
}
