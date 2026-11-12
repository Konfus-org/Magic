using Magic.Graphics;
using Magic.Platform;
using Magic.Projects;
using Magic.Threading;

namespace Magic.VulkanGraphics;

/// <summary>
/// The gem: it makes the <see cref="GraphicsApi"/> over its <see cref="Settings"/>.
/// </summary>
public sealed class EntryPoint(
    VulkanGraphicsSettings settings,
    App app,
    Threads threads,
    IShaderCompiler compiler,
    IWindow? window = null
) : IGem, IDisposable
{
    /// <summary>
    /// How frames reach the window, and the GPU that draws them. Game thread.
    /// </summary>
    public VulkanGraphicsSettings Settings { get; } = settings;

    public GraphicsApi GraphicsApi { get; } = new(settings, app, threads, compiler, window);

    public void Dispose() => GraphicsApi.Dispose();
}
