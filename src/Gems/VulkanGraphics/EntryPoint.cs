using Magic.Graphics;
using Magic.Platform;
using Magic.Projects;
using Magic.Threading;

namespace Magic.VulkanGraphics;

/// <summary>
/// The gem: it makes the <see cref="GraphicsApi"/> over its <see cref="Settings"/>. It takes the shader compiler, and
/// the window when a gem provides one, so it is made after those gems. It is public because nothing in its assembly
/// makes it: Magic does, by reflection, when a project lists the gem.
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

    /// <summary>
    /// The GPU through Vulkan.
    /// </summary>
    public GraphicsApi GraphicsApi { get; } = new(settings, app, threads, compiler, window);

    public void Dispose() => GraphicsApi.Dispose();
}
