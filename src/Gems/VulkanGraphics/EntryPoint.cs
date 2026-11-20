using Magic.CommandLine;
using Magic.Events;
using Magic.Files;
using Magic.Graphics;
using Magic.Platform;
using Magic.Projects;
using Magic.Threading;
using Magic.Utils;

namespace Magic.VulkanGraphics;

/// <summary>
/// The gem: it makes the <see cref="GraphicsApi"/> over its <see cref="Settings"/>, adds the <c>gpu</c> subject of
/// <c>query</c>, which prints the GPU that draws, and gives the graphics API each RenderDoc capture asked for.
/// </summary>
public sealed class EntryPoint : Disposable, IGem
{
    public EntryPoint(
        VulkanGraphicsSettings settings,
        App app,
        Threads threads,
        IShaderCompiler compiler,
        ConsoleCommands commands,
        Caches caches,
        IWindow? window = null
    )
    {
        Settings = settings;
        GraphicsApi = new(settings, app, threads, compiler, caches, window);
        Track(GraphicsApi);

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

    public void Update(in Frame frame)
    {
        foreach (CaptureRequested asked in frame.Events.Get<CaptureRequested>())
        {
            GraphicsApi.Capture(asked);
        }
    }
}
