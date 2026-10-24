using Magic.Contexts.Assets;
using Magic.Contexts.Frames;
using Magic.Contexts.Gpu;
using Magic.Contexts.Rendering;
using Magic.Contexts.Utils;

namespace Magic.Interfaces;

/// <summary>
/// The renderer, as a pipeline script (<see cref="IRenderPipeline"/>) and a post effect (<see cref="IPostEffect"/>)
/// record a frame with: what the frame is drawn from, the resources it asks for, the renderer's steps for drawing the
/// world, which the script puts in order and gives its targets, and passes of its own shaders. Provided by a renderer
/// gem. Everything but reading back is the render thread's, while a frame is recorded.
/// <para>
/// A texture or buffer asked for is this frame's alone: one the same as it, left by an earlier frame, is handed out
/// again, its contents what that frame left, and one no frame asks for is let go. What lasts from frame to frame is the
/// renderer's: each view's depth pyramid and exposure, its culling, and the sky's light. A pass whose shader is not
/// compiled yet is left out, which makes the frame incomplete: it is not shown, the window keeping the last whole one.
/// Each pass is a debug group of its own, which the GPU provider times.
/// </para>
/// </summary>
public interface IRendering
{
    Frame Time { get; }

    /// <summary>
    /// The cameras drawn this frame, each into its target.
    /// </summary>
    IReadOnlyList<RenderView> Views { get; }

    /// <summary>
    /// The world's post effects, in order: its <see cref="Contexts.Components.PostProcessing"/>'s, or none.
    /// </summary>
    RenderEffects<IPostEffect> PostEffects { get; }

    /// <summary>
    /// The world's weather: its <see cref="Contexts.Components.Weather"/>'s effects, or none.
    /// </summary>
    RenderEffects<IWeatherEffect> Weather { get; }

    /// <summary>
    /// The frame's failure codes, a word each, which a pass that raises one (Shaders/Core/Failure.slang) lists among
    /// its writes, and which the renderer says on the failure banner.
    /// </summary>
    GpuBuffer Failures { get; }

    GpuTexture Texture(in GpuTextureDescription description);

    GpuBuffer Buffer(uint bytes);

    /// <summary>
    /// The sampler <paramref name="description"/> describes, made the first time it is asked for and kept.
    /// </summary>
    GpuSampler Sampler(in GpuSamplerDescription description);

    /// <summary>
    /// What of the world <paramref name="view"/> sees, culled in <paramref name="phase"/>: the first against the depth
    /// pyramid the view last built (<see cref="BuildDepthPyramid"/>), the second, what the first hid, against the one
    /// built since.
    /// </summary>
    VisibleWorld Cull(RenderView view, CullPhase phase);

    /// <summary>
    /// Draws what a culling found into <paramref name="ids"/>, a <see cref="GpuFormat.R32Uint"/> texture of the view's
    /// size, each pixel naming the triangle in front, and <paramref name="depth"/>, their contents as
    /// <paramref name="contents"/> says.
    /// </summary>
    void DrawVisibility(
        RenderView view,
        in VisibleWorld visible,
        GpuTexture ids,
        GpuTexture depth,
        GpuContents contents);

    /// <summary>
    /// Builds <paramref name="view"/>'s depth pyramid from <paramref name="depth"/>, which the next culling tests
    /// against.
    /// </summary>
    void BuildDepthPyramid(RenderView view, GpuTexture depth);

    /// <summary>
    /// Sorts the view's tiles by the surfaces their pixels show in <paramref name="ids"/>, for
    /// <see cref="Resolve"/>.
    /// </summary>
    SurfaceTiles Classify(RenderView view, GpuTexture ids);

    /// <summary>
    /// Resolves each pixel's surface into <paramref name="targets"/>, each surface drawn over its tiles, its pipelines
    /// made for the targets' formats.
    /// </summary>
    void Resolve(RenderView view, in SurfaceTiles tiles, GpuTexture ids, in GBuffer targets);

    /// <summary>
    /// The sky's light, seen from <paramref name="seenFrom"/>'s camera: captured into a cube, its light from everywhere
    /// as harmonics, and its reflections for each roughness.
    /// </summary>
    SkyLight CaptureSky(RenderView seenFrom);

    /// <summary>
    /// Draws the sky into <paramref name="target"/> where <paramref name="depth"/> has nothing. A compute pass writes
    /// <paramref name="target"/>, so it needs <see cref="GpuTextureUsage.ComputeWrite"/>.
    /// </summary>
    void DrawSky(RenderView view, GpuTexture depth, GpuTexture target);

    /// <summary>
    /// The world as voxels around the player's camera, brought up to date where it changed since the last frame: once a
    /// frame, before the views that trace it.
    /// </summary>
    VoxelScene VoxelizeScene();

    /// <summary>
    /// The point and spot lights <paramref name="view"/> sees, sorted by depth and binned by depth slice and tile, which
    /// a pixel walks to find the lights that reach it.
    /// </summary>
    LightList CullLights(RenderView view);

    /// <summary>
    /// A compute pass of <paramref name="shader"/>'s entry point drawn for <paramref name="view"/>, its constants the
    /// pass header (Shaders/Core/Pass.slang), then <paramref name="constants"/>, of <paramref name="groups"/>, which
    /// reads and writes what it says.
    /// </summary>
    void Dispatch<T>(
        RenderView view,
        Handle<Shader> shader,
        in T constants,
        GpuGroups groups,
        ReadOnlySpan<GpuResource> reads,
        ReadOnlySpan<GpuResource> writes)
        where T : unmanaged;

    /// <summary>
    /// A compute pass of <paramref name="shader"/>'s entry point drawn for <paramref name="view"/>, as the dispatch
    /// above, whose groups the GPU reads from <paramref name="arguments"/> at <paramref name="offset"/>, three
    /// <c>uint</c>s an earlier pass wrote.
    /// </summary>
    void Dispatch<T>(
        RenderView view,
        Handle<Shader> shader,
        in T constants,
        GpuBuffer arguments,
        uint offset,
        ReadOnlySpan<GpuResource> reads,
        ReadOnlySpan<GpuResource> writes)
        where T : unmanaged;

    /// <summary>
    /// <paramref name="image"/> run through <paramref name="shader"/> into a texture like it, a thread a pixel in
    /// groups of 8 by 8: its constants the pass header, Shaders/Core/Post.slang's <c>PostImages</c>, then
    /// <paramref name="values"/>. For an effect that reads a pixel's neighbours; one that changes each pixel by itself is
    /// an <see cref="IPixelEffect"/>.
    /// </summary>
    GpuTexture Effect<T>(RenderView view, Handle<Shader> shader, GpuTexture image, in T values)
        where T : unmanaged;

    /// <summary>
    /// <paramref name="image"/> run through <paramref name="effects"/> in order into a texture like it, as one pass
    /// while their values fit its constants, and the image after them.
    /// </summary>
    GpuTexture ApplyPixelEffects(RenderView view, GpuTexture image, ReadOnlySpan<IPixelEffect> effects);

    /// <summary>
    /// <paramref name="image"/> run through the world's post effects in order (<see cref="PostEffects"/>), each run of
    /// pixel effects as one pass (<see cref="ApplyPixelEffects"/>), and the image after them.
    /// </summary>
    GpuTexture ApplyPostEffects(RenderView view, GpuTexture image);

    /// <summary>
    /// Shows <paramref name="image"/> as <paramref name="view"/>'s finished image, scaled into its part of its target
    /// and encoded as the target expects.
    /// </summary>
    void Output(RenderView view, GpuTexture image);

    /// <summary>
    /// The window's next image as it is shown, the overlay over it. From any thread; fails when there is no window
    /// image to read (headless), or the renderer stops first.
    /// </summary>
    Task<Result<GpuImage>> CaptureAsync(CancellationToken cancel);

    /// <summary>
    /// Level 0, layer 0 of <paramref name="texture"/>, tightly packed rows top to bottom, as the frame that asked for
    /// it left it, read once the GPU has finished that frame, waiting for it that once. From any thread, for checks and
    /// tools.
    /// </summary>
    Task<Result<GpuImage>> ReadAsync(GpuTexture texture, CancellationToken cancel);

    /// <summary>
    /// The first <paramref name="bytes"/> of <paramref name="buffer"/> as the frame that asked for it left them, read
    /// as <see cref="ReadAsync(GpuTexture, CancellationToken)"/> reads a texture.
    /// </summary>
    Task<Result<byte[]>> ReadAsync(GpuBuffer buffer, uint bytes, CancellationToken cancel);
}
