using Magic.Assets;
using Magic.Files;
using Magic.Projects;
using Magic.Threading;
using Magic.Utils;
using Magic.Worlds;

namespace Magic.SdlPlatform;

/// <summary>
/// The gem: the window, clipboard and input through SDL3, over one <see cref="SdlWindow"/>.
/// </summary>
/// <remarks>
/// The platform thread, which is the process's first thread, makes it, because SDL's windows and events belong to that
/// thread. Each frame, <see cref="Update"/> lets the window pace the frame, then makes the input the frame's input.
/// </remarks>
public sealed class EntryPoint : Disposable, IGem
{
    public EntryPoint(
        SdlPlatformSettings settings,
        App app,
        Threads threads,
        AssetManager assets,
        IFileSystem files,
        World world
    )
    {
        Settings = settings;
        // Tracked in creation order, so SDL ends last, after the input and platform.
        SdlWindow sdl = new(settings, app.WindowName, app.IsHeadless);
        Track(sdl);
        Window = new Window(sdl, settings, app.WindowName, app, threads, assets);
        Clipboard = new Clipboard(sdl, threads);
        Input = new Input(sdl, settings, app, world);
        Track(Input);
        Platform = new Platform(sdl, files);
        Track(Platform);
    }

    /// <summary>
    /// The window's and input's settings.
    /// </summary>
    /// <remarks>
    /// Set them only on the game thread. The platform thread shows a new window mode, display or resizability.
    /// </remarks>
    public SdlPlatformSettings Settings { get; }

    public Window Window { get; }

    public Clipboard Clipboard { get; }

    public Input Input { get; }

    public Platform Platform { get; }

    public void Update(in Frame frame)
    {
        Window.Update();
        Input.Update(frame);
    }
}
