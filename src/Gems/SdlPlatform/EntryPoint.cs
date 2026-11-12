using Magic.Assets;
using Magic.Files;
using Magic.Projects;
using Magic.Threading;
using Magic.Worlds;

namespace Magic.SdlPlatform;

/// <summary>
/// The gem: the window, clipboard and input, through SDL3, over one <see cref="SdlWindow"/>. The process's first thread,
/// the platform thread, makes it, as SDL's windows and events belong to that thread. Each frame, <see cref="Update"/>
/// lets the window pace the frame, then makes input the frame's.
/// </summary>
public sealed class EntryPoint : IGem, IDisposable
{
    private readonly SdlWindow _sdl;

    public EntryPoint(
        SdlPlatformSettings settings,
        App app,
        Threads threads,
        AssetManager assets,
        IFileSystem files,
        World world
    )
    {
        string title = app.Project?.Name ?? string.Empty;
        Settings = settings;
        _sdl = new SdlWindow(settings, title, app.IsHeadless);
        Window = new Window(_sdl, settings, title, app, threads, assets);
        Clipboard = new Clipboard(_sdl, threads);
        Input = new Input(_sdl, settings, app, world);
        Platform = new Platform(_sdl, files);
    }

    /// <summary>
    /// The window's and input's settings. A new window mode, display or resizability is shown on the platform thread.
    /// Game thread.
    /// </summary>
    public SdlPlatformSettings Settings { get; }

    public Window Window { get; }

    public Clipboard Clipboard { get; }

    public Input Input { get; }

    public Platform Platform { get; }

    public void Dispose()
    {
        Platform.Dispose();
        Input.Dispose();
        _sdl.Dispose();
    }

    public void Update(in Frame frame)
    {
        Window.Update();
        Input.Update(frame);
    }
}
