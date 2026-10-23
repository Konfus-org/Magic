using System.IO;
using Magic.Contexts.Assets;
using Magic.Contexts.Gpu;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.VoxelRendering;

/// <summary>
/// Records the frame's passes of one shader: a compute pass, its groups given or read from a buffer, and a fullscreen
/// pass composed with the world's sky; each a debug group named after its shader's file, which the GPU provider
/// times. A pass whose pipeline is not made yet is left out, which makes the frame incomplete
/// (<see cref="GpuPipelines.IsFrameComplete"/>). Render thread.
/// </summary>
internal sealed class Passes(IGpuDriver gpu, GpuPipelines pipelines, Assets assets)
{
    /// <summary>
    /// Each shader's debug group's name, its file's name up to its first dot.
    /// </summary>
    private readonly Dictionary<Handle<Shader>, string> _names = [];

    /// <summary>
    /// The world's sky's surface, which a fullscreen pass is composed with: this frame's.
    /// </summary>
    internal SurfaceKey Sky { get; set; }

    internal void Dispatch<T>(
        Handle<Shader> shader,
        in T constants,
        GpuGroups groups,
        ReadOnlySpan<GpuResource> reads,
        ReadOnlySpan<GpuResource> writes)
        where T : unmanaged
    {
        GpuPipeline pipeline = pipelines.GetCompute(shader);

        if (pipeline == GpuPipeline.None || groups.X == 0 || groups.Y == 0 || groups.Z == 0) return;

        using GpuDebugGroup group = gpu.Commands.DebugGroup(NameOf(shader));
        using GpuComputePass pass = gpu.Commands.ComputePass();
        pass.Reads(reads).Writes(writes).Pipeline(pipeline).Constants(constants).Dispatch(groups.X, groups.Y, groups.Z);
    }

    internal void Dispatch<T>(
        Handle<Shader> shader,
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
        pass.Reads(reads).Writes(writes).Pipeline(pipeline).Constants(constants).DispatchFromBuffer(arguments, offset);
    }

    /// <summary>
    /// A render pass that draws <paramref name="shader"/>, composed with the world's sky, over the whole of
    /// <paramref name="target"/>, its contents as <paramref name="contents"/> says, which reads what it says.
    /// </summary>
    internal void Fullscreen<T>(
        Handle<Shader> shader,
        in T constants,
        GpuTextureLevel target,
        GpuContents contents,
        ReadOnlySpan<GpuResource> reads)
        where T : unmanaged
    {
        GpuPipeline pipeline = pipelines.GetFullscreen(shader, Sky, gpu.GetFormat(target.Texture));

        if (pipeline == GpuPipeline.None) return;

        using GpuDebugGroup group = gpu.Commands.DebugGroup(NameOf(shader));
        using GpuRenderPass render = gpu.Commands.RenderPass([target], contents);
        render.Reads(reads).Pipeline(pipeline).Constants(constants).Draw(3);
    }

    private string NameOf(Handle<Shader> shader)
    {
        if (!_names.TryGetValue(shader, out string? name))
        {
            string file = Path.GetFileName(assets.GetPath(shader) ?? $"Shader {shader.Id}");
            int dot = file.IndexOf('.', StringComparison.Ordinal);
            name = dot > 0 ? file[..dot] : file;
            _names[shader] = name;
        }

        return name;
    }
}
