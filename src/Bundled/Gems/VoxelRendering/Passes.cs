using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Magic.Contexts.Assets;
using Magic.Contexts.Gpu;
using Magic.Contexts.Rendering;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.VoxelRendering;

/// <summary>
/// Records the frame's compute passes, for a view or for the whole frame, of one entry point each, their groups given
/// or read from a buffer. Each is handed its header, the frame's constants and the view's, none for a pass of the whole
/// frame (<see cref="PassHeader"/>), ahead of its own, and is a debug group named after its entry point, or its
/// shader's file for one with a single entry point, which the GPU provider times. A pass whose pipeline is not made yet is left out, which makes the frame incomplete
/// (<see cref="GpuPipelines.IsFrameComplete"/>). Render thread.
/// </summary>
internal sealed class Passes(IGpuDriver gpu, GpuPipelines pipelines, Assets assets, GpuBuffer world)
{
    /// <summary>
    /// The most bytes of constants a draw or dispatch pushes.
    /// </summary>
    internal const int MaxConstantBytes = 128;

    /// <summary>
    /// Each single-entry shader's debug group's name, its file's name up to its first dot.
    /// </summary>
    private readonly Dictionary<Handle<Shader>, string> _names = [];

    /// <summary>
    /// The header of a pass drawn for <paramref name="view"/>.
    /// </summary>
    internal PassHeader GetHeader(RenderView view) => new(world, view.Constants);

    /// <summary>
    /// The header of a pass of the whole frame, drawn for no view: its passes read no view's constants.
    /// </summary>
    internal PassHeader GetFrameHeader() => new(world, GpuBuffer.None);

    internal void Dispatch<T>(
        RenderView view,
        ShaderEntry shader,
        in T constants,
        GpuGroups groups,
        ReadOnlySpan<GpuResource> reads,
        ReadOnlySpan<GpuResource> writes)
        where T : unmanaged
        => Dispatch(GetHeader(view), shader, constants, groups, reads, writes);

    internal void Dispatch<T>(
        in PassHeader header,
        ShaderEntry shader,
        in T constants,
        GpuGroups groups,
        ReadOnlySpan<GpuResource> reads,
        ReadOnlySpan<GpuResource> writes)
        where T : unmanaged
        => Dispatch(header, shader, MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in constants)), groups, reads, writes);

    /// <summary>
    /// A compute pass whose constants after its header are <paramref name="constants"/>, as bytes.
    /// </summary>
    internal void Dispatch(
        in PassHeader header,
        ShaderEntry shader,
        ReadOnlySpan<byte> constants,
        GpuGroups groups,
        ReadOnlySpan<GpuResource> reads,
        ReadOnlySpan<GpuResource> writes)
    {
        GpuPipeline pipeline = pipelines.GetCompute(shader);

        if (pipeline == GpuPipeline.None || groups.X == 0 || groups.Y == 0 || groups.Z == 0) return;

        using GpuDebugGroup group = gpu.Commands.DebugGroup(NameOf(shader));
        using GpuComputePass pass = gpu.Commands.ComputePass();
        pass.Reads(reads)
            .Writes(writes)
            .Pipeline(pipeline)
            .Constants(WithHeader(header, constants, stackalloc byte[MaxConstantBytes]))
            .Dispatch(groups.X, groups.Y, groups.Z);
    }

    internal void Dispatch<T>(
        RenderView view,
        ShaderEntry shader,
        in T constants,
        GpuBuffer arguments,
        uint offset,
        ReadOnlySpan<GpuResource> reads,
        ReadOnlySpan<GpuResource> writes)
        where T : unmanaged
        => Dispatch(GetHeader(view), shader, constants, arguments, offset, reads, writes);

    internal void Dispatch<T>(
        in PassHeader header,
        ShaderEntry shader,
        in T constants,
        GpuBuffer arguments,
        uint offset,
        ReadOnlySpan<GpuResource> reads,
        ReadOnlySpan<GpuResource> writes)
        where T : unmanaged
    {
        GpuPipeline pipeline = pipelines.GetCompute(shader);

        if (pipeline == GpuPipeline.None) return;

        using GpuDebugGroup group = gpu.Commands.DebugGroup(NameOf(shader));
        using GpuComputePass pass = gpu.Commands.ComputePass();
        pass.Reads(reads)
            .Writes(writes)
            .Pipeline(pipeline)
            .Constants(new PassConstants<T>(header, constants))
            .DispatchFromBuffer(arguments, offset);
    }

    /// <summary>
    /// <paramref name="header"/> and then <paramref name="constants"/>, written into <paramref name="into"/>.
    /// </summary>
    private static ReadOnlySpan<byte> WithHeader(in PassHeader header, ReadOnlySpan<byte> constants, Span<byte> into)
    {
        MemoryMarshal.Write(into, in header);
        int size = Unsafe.SizeOf<PassHeader>();
        constants.CopyTo(into[size..]);

        return into[..(size + constants.Length)];
    }

    private string NameOf(ShaderEntry shader)
    {
        if (shader.EntryPoint is { } entryPoint) return entryPoint;

        if (!_names.TryGetValue(shader.Module, out string? name))
        {
            string file = Path.GetFileName(assets.GetPath(shader.Module) ?? $"Shader {shader.Module.Id}");
            int dot = file.IndexOf('.', StringComparison.Ordinal);
            name = dot > 0 ? file[..dot] : file;
            _names[shader.Module] = name;
        }

        return name;
    }
}
