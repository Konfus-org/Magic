using Magic.Assets;
using Magic.Projects;
using Magic.Threading;

namespace Magic.TaaUpscaling;

/// <summary>
/// Upscales the finished picture with temporal anti-aliasing.
/// </summary>
public sealed class EntryPoint(AssetManager assets, Threads threads) : IGem
{
    public Renderer Renderer { get; } = new(assets, threads);
}
