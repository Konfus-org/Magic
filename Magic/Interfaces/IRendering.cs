using Magic.Contexts.Rendering;
using Magic.Utils;

namespace Magic.Interfaces;

/// <summary>
/// The GPU, one level above the graphics API: make objects, fill them, run a frame's commands. Exported by a renderer gem
/// (SDL GPU today; Vulkan or anything else would implement the same). It keeps no caches and knows nothing of assets,
/// entities or windows beyond presenting to them: the host's render system and any gem that draws decide everything and
/// hold every handle. The gem is static: loaded once, never hot reloaded, so a handle is good for the whole run.
/// Render thread only (<see cref="Contexts.ThreadId.Render"/>: a gem's constructor, Dispose and Render hook are on
/// it), except <see cref="Compile"/>.
/// </summary>
public interface IRendering
{
    /// <summary>
    /// Slow, loud checks for development: GPU validation, and Core's convention probes and culling verification. Core sets it
    /// (in its Debug builds) as soon as the gem is constructed, before anything else can call it; a renderer that can only
    /// turn validation on when its device is made makes the device on first use.
    /// </summary>
    bool Debug { get; set; }

    /// <summary>
    /// The GPU's name, for logs.
    /// </summary>
    string Device { get; }

    /// <summary>
    /// The bytecode format <see cref="Compile"/> produces (e.g. <c>SPIRV</c>, <c>DXIL</c>): part of a shader cache key.
    /// </summary>
    string ShaderFormat { get; }

    /// <summary>
    /// The depth format this device renders depth targets in.
    /// </summary>
    GpuFormat DepthFormat { get; }

    GpuBuffer CreateBuffer(GpuBufferUsage usage, uint bytes);

    GpuTexture CreateTexture(in TextureDesc desc);

    GpuSampler CreateSampler(in SamplerDesc desc);

    /// <summary>
    /// HLSL (entry point <c>main</c>) to this device's bytecode, with its binding counts. <paramref name="includeDirectory"/>
    /// is where <c>#include</c>s are looked up. The renderer defines the stage in front of the source (<c>STAGE_VERTEX</c>,
    /// <c>STAGE_FRAGMENT</c> or <c>STAGE_COMPUTE</c>), which <c>Include/Bindings.hlsli</c> picks its registers by. Pure and
    /// thread-safe; the caller caches the result. Failures carry the compiler's text.
    /// </summary>
    Result<CompiledShader> Compile(string hlsl, string name, GpuStage stage, string includeDirectory);

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
    /// milliseconds spent blocked on the GPU (an older frame, or a swapchain image).
    /// </summary>
    float Submit(RenderCommands commands);

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
