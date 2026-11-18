using Magic.CommandLine;
using Magic.Graphics;
using Magic.Platform;
using Magic.Projects;
using Magic.Threading;

namespace Magic.VulkanGraphics;

/// <summary>
/// The gem: it makes the <see cref="GraphicsApi"/> over its <see cref="Settings"/>, and adds the <c>gpu</c> subject of
/// <c>query</c>, which prints the GPU that draws.
/// </summary>
public sealed class EntryPoint : IGem, IDisposable
{
    public EntryPoint(
        VulkanGraphicsSettings settings,
        App app,
        Threads threads,
        IShaderCompiler compiler,
        ConsoleCommands commands,
        IWindow? window = null
    )
    {
        Settings = settings;
        GraphicsApi = new(settings, app, threads, compiler, window);
        commands.RegisterQuery(
            "gpu",
            "Prints the GPU that draws: its name, its kind, its driver, the API, its memory and whether the API validates",
            GraphicsApi.Describe
        );
    }

    /// <summary>
    /// How frames reach the window, and the GPU that draws them. Game thread.
    /// </summary>
    public VulkanGraphicsSettings Settings { get; }

    public GraphicsApi GraphicsApi { get; }

    public void Dispose() => GraphicsApi.Dispose();
}
