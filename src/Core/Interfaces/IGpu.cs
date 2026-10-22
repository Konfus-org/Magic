using System.Drawing;
using Magic.Contexts.Frames;
using Magic.Contexts.Gpu;
using Magic.Contexts.Threading;
using Magic.Contexts.Utils;
using Magic.Services;

namespace Magic.Interfaces;

/// <summary>
/// The GPU, one level above the graphics API: make objects, fill them, and record a frame's commands into
/// <see cref="Commands"/>, which the provider submits and presents in its <see cref="Phase.Render"/> hook. It keeps no
/// caches and knows nothing of assets, entities or what it draws: whoever draws decides everything and holds every
/// handle.
/// <para>
/// It is the render thread's (<see cref="EngineThread.Render"/>), which made the windows: gem constructors and
/// Dispose, the Render hooks, and work handed to the render thread (<see cref="Threads.InvokeAsync{T}"/>, as a renderer
/// fits the passes a worker loaded). Anywhere else it throws.
/// </para>
/// <para>
/// A render target is a texture: one the drawer made (a render texture, which can then be sampled, as an editor's
/// viewport is), or a window's (<see cref="GetWindowTarget"/>). Whatever draws asks it for its <see cref="GetFormat"/>
/// and <see cref="GetSize"/> and treats the two alike.
/// </para>
/// <para>
/// The provider is made before every gem that takes it, so its Render runs after theirs: take it, and record in
/// Render. Shaders are HLSL compiled for it by an <see cref="IShaderCompiler"/> into <see cref="ShaderFormat"/>, in
/// the binding model of Shaders/Include/Bindings.hlsli. A released object lives on until no frame in flight can still
/// use it. Making a buffer, texture or sampler that fails throws, being a caller's mistake (a format the GPU cannot use
/// that way) or the machine out of memory; a pipeline is a <see cref="Result{T}"/>, its shaders being content.
/// </para>
/// </summary>
public interface IGpu
{
    /// <summary>
    /// The GPU and the API driving it, for logs: <c>NVIDIA GeForce RTX 4070 (vulkan)</c>.
    /// </summary>
    string Name { get; }

    GpuShaderFormat ShaderFormat { get; }

    /// <summary>
    /// This frame's commands, submitted and cleared in the provider's <see cref="Phase.Render"/>.
    /// </summary>
    GpuCommands Commands { get; }

    GpuBuffer CreateBuffer(GpuBufferUsage usage, uint bytes);

    GpuTexture CreateTexture(in GpuTextureDescription description);

    GpuSampler CreateSampler(in GpuSamplerDescription description);

    /// <summary>
    /// A graphics pipeline, or why the GPU would not make one of these shaders.
    /// </summary>
    Result<GpuPipeline> CreatePipeline(GpuPipelineDescription description);

    /// <summary>
    /// A compute pipeline, or why the GPU would not make one of this shader.
    /// </summary>
    Result<GpuPipeline> CreatePipeline(GpuShader compute);

    /// <summary>
    /// A window as a render target: a texture that is, each frame, the image the window shows next, acquired the first
    /// time a command draws into it and shown once the frame is submitted. The same texture every call for one window;
    /// <see cref="Release(GpuTexture)"/> gives the window back, before it closes, and it can be a target again from the
    /// next frame. It is drawn into, never sampled or read: draw into a texture of your own for that. Its format
    /// (<see cref="GetFormat"/>) says who encodes its colour: an UNORM image's the last pass of a frame encodes, an sRGB
    /// one's the GPU does. While the window has no image (minimised, or none at all in a headless run) it is 0 x 0, and
    /// the passes into it are skipped.
    /// </summary>
    GpuTexture GetWindowTarget(IWindow window);

    /// <summary>
    /// What a texture was made with, or a window's image format: what the pipelines that draw into it are made for.
    /// <see cref="GpuFormat.Invalid"/> for a window with no image and for what is no texture.
    /// </summary>
    GpuFormat GetFormat(GpuTexture texture);

    /// <summary>
    /// Level 0's width and height in pixels, a window's as it is now: what whatever draws into a target sizes its own
    /// textures by.
    /// </summary>
    Size GetSize(GpuTexture texture);

    /// <summary>
    /// Copies <paramref name="data"/> now; it reaches the buffer at the start of the next submit, before that frame's
    /// commands, in call order with the other uploads.
    /// </summary>
    void Upload(GpuBuffer buffer, uint offset, ReadOnlySpan<byte> data);

    /// <summary>
    /// Tightly packed texels for one region of a texture, queued as <see cref="Upload(GpuBuffer, uint,
    /// ReadOnlySpan{byte})"/> is.
    /// </summary>
    void Upload(in GpuTextureRegion region, ReadOnlySpan<byte> data);

    void Release(GpuBuffer buffer);

    void Release(GpuTexture texture);

    void Release(GpuSampler sampler);

    void Release(GpuPipeline pipeline);

    /// <summary>
    /// The first <paramref name="bytes"/> of a buffer, after everything submitted so far. Waits for the GPU: for
    /// checks and tools, never the frame loop.
    /// </summary>
    Result<byte[]> Read(GpuBuffer buffer, uint bytes);

    /// <summary>
    /// Level 0, layer 0 of a texture, tightly packed rows top to bottom, after everything submitted so far; never a
    /// window's, which <see cref="CaptureAsync"/> reads. Waits for the GPU: for checks and tools, never the frame loop.
    /// </summary>
    Result<byte[]> Read(GpuTexture texture);

    /// <summary>
    /// A window's next image, as it is shown: the next frame that draws into <paramref name="window"/> draws into an
    /// image of its own, which is copied into the window and read back, waiting for the GPU that once. For screenshots,
    /// never the frame loop. Fails for a target that is no window's or one that never has an image (headless), and when
    /// the GPU stops first.
    /// </summary>
    Task<Result<GpuImage>> CaptureAsync(GpuTexture window);
}
