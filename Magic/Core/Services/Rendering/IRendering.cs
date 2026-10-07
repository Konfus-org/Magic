using Magic.Contexts.Rendering;
using Magic.Contexts.Threading;
using Magic.Utils;

namespace Magic.Interfaces;

/// <summary>
/// The GPU, one level above the graphics API: make objects, fill them, run a frame's commands. Exported by a renderer gem
/// (SDL GPU today; Vulkan or anything else would implement the same). It keeps no caches and knows nothing of assets,
/// entities or windows beyond presenting to them: the DeferredRenderer gem's RenderSystem and any gem that draws
/// decide everything and hold every handle. The gem is static: loaded once, never hot reloaded, so a handle is good
/// for the whole run.
/// Render thread only (<see cref="ThreadId.Render"/>: a gem's constructor, Dispose and Render hook are on
/// it). Shaders are compiled for it by an <see cref="IShaderCompiler"/>, into its <see cref="ShaderFormat"/>.
/// </summary>
public interface IRendering
{
    /// <summary>
    /// Slow, loud checks for development: GPU validation, and a renderer's convention probes and culling verification.
    /// On in the renderer gem's Debug builds.
    /// </summary>
    bool Debug { get; }

    /// <summary>
    /// The frame's list, render thread only: gems record into it in their Render and Present hooks (what depends on
    /// the renderer draws on top), and the renderer submits it in its own Present, the last, and clears it.
    /// </summary>
    RenderCommands Commands { get; }

    /// <summary>
    /// The GPU's name, for logs.
    /// </summary>
    string Device { get; }

    /// <summary>
    /// The bytecode format this device takes (e.g. <c>SPIRV</c>, <c>DXIL</c>): what <see cref="IShaderCompiler"/> compiles to,
    /// and part of a shader cache key.
    /// </summary>
    string ShaderFormat { get; }

    /// <summary>
    /// Moves on whenever the device is made again (another backend was chosen): every GPU object made before is gone,
    /// so whoever holds any makes them again, without releasing the old.
    /// </summary>
    uint Generation { get; }

    /// <summary>
    /// The depth format this device renders depth targets in.
    /// </summary>
    GpuFormat DepthFormat { get; }

    GpuBuffer CreateBuffer(GpuBufferUsage usage, uint bytes);

    GpuTexture CreateTexture(in TextureDesc desc);

    GpuSampler CreateSampler(in SamplerDesc desc);

    GpuPipeline CreatePipeline(PipelineDesc desc);

    GpuPipeline CreateComputePipeline(CompiledShader shader);

    /// <summary>
    /// Copies <paramref name="data"/> now; it reaches the buffer at the start of the next <see cref="Submit"/>, in call order with the other uploads and copies.
    /// </summary>
    void Upload(GpuBuffer buffer, uint offset, ReadOnlySpan<byte> data);

    /// <summary>
    /// Tightly packed pixels for one region of a texture, queued like the buffer upload.
    /// </summary>
    void Upload(in TextureRegion region, ReadOnlySpan<byte> data);

    /// <summary>
    /// A texture region (its size) into another, queued in order with the uploads.
    /// </summary>
    void Copy(in TextureRegion source, in TextureRegion destination);

    /// <summary>
    /// <paramref name="bytes"/> of one buffer into another, queued in order with the uploads.
    /// </summary>
    void Copy(GpuBuffer source, uint sourceOffset, GpuBuffer destination, uint destinationOffset, uint bytes);

    /// <summary>
    /// Released once no frame in flight can still use it. 0 does nothing.
    /// </summary>
    void Release(GpuBuffer buffer);

    void Release(GpuTexture texture);

    void Release(GpuSampler sampler);

    void Release(GpuPipeline pipeline);

    /// <summary>
    /// The format of the window's swapchain images, for pipelines that draw into them; <see cref="GpuFormat.Invalid"/> when it is not open.
    /// </summary>
    GpuFormat WindowFormat(uint window);

    /// <summary>
    /// Runs the queued uploads, then <paramref name="commands"/> in order, then presents every window a command drew into,
    /// and clears the list. A window that has no image this frame (minimised) skips the commands into it. Returns the
    /// milliseconds spent blocked on the GPU (an older frame, or a swapchain image). The renderer's Present runs
    /// <see cref="Commands"/> through it; called directly, it runs any other list (a probe's), for a synchronous check
    /// before the frame records.
    /// </summary>
    float Submit(RenderCommands commands);

    /// <summary>
    /// Runs <paramref name="action"/> once on the render thread right after the next submit: to read back what that
    /// frame showed (a screenshot).
    /// </summary>
    void AfterSubmit(Action action);

    /// <summary>
    /// The start of a buffer, after everything submitted. Waits for the GPU: debug checks only.
    /// </summary>
    Result<byte[]> Read(GpuBuffer buffer, uint bytes);

    /// <summary>
    /// Level 0 of an RGBA8 texture, after everything submitted; for a <see cref="GpuTexture.Window"/>, the texture last
    /// blitted onto that window, failed when none has been. Waits for the GPU: screenshots and debug checks only.
    /// </summary>
    Result<CapturedFrame> Read(GpuTexture texture);
}
