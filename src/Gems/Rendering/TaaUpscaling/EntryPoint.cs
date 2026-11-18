using Magic.Assets;
using Magic.Graphics;
using Magic.Projects;
using Magic.Threading;

namespace Magic.TaaUpscaling;

/// <summary>
/// Upscales the finished picture with temporal anti-aliasing, and sharpens it as much as its <see cref="Settings"/>
/// say.
/// </summary>
public sealed class EntryPoint(TaaUpscalingSettings settings, AssetManager assets, Threads threads, IGraphicsApi gpu)
    : IGem
{
    /// <summary>
    /// How much the picture is sharpened, which the renderer reads each frame. Game thread.
    /// </summary>
    public TaaUpscalingSettings Settings { get; } = settings;

    public Renderer Renderer { get; } = new(settings, assets, threads, gpu);
}
