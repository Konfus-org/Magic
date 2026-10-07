using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Interfaces;
using Magic.Services;
using Magic.Utils;
using SDL3;

namespace SdlPlatformGem;

/// <summary>
/// The gem: the platform on SDL, one class in three files. Here, SDL itself: <c>SDL_Init</c> of video and gamepads
/// on load, <c>SDL_Quit</c> on unload, SDL's log routed into <see cref="Log"/>, and the event pump every frame, which
/// also latches the input. <c>SdlPlatform.Windows.cs</c> has the windows (<see cref="IWindowRegistry"/>)
/// and the clipboard (<see cref="IClipboard"/>); <c>SdlPlatform.Input.cs</c> has
/// keyboard, mouse and gamepads (<see cref="IInput"/>) and publishes their events. The main window opens as the gem
/// loads, as <see cref="WindowSettings"/> say, and closing it ends the game.
/// <para>
/// The OS ties a window to the thread that made it, which is the render thread: the gem is constructed there, and
/// pumps there in its Render hook, while the main thread waits. Each event updates the windows and the live input as
/// it comes out of the queue; then that input is made what every reader sees next frame.
/// </para>
/// </summary>
internal sealed partial class SdlPlatform : IGem
{
    // Kept in a field: SDL holds the native function pointer, and a collected delegate would crash the next call.
    private readonly SDL.LogOutputFunction _logOutput = OnLog;

    private readonly Assets _assets;
    private readonly Events _events;
    private readonly Threads _threads;
    private readonly Handle<Texture> _projectIcon;

    public SdlPlatform(Project project, Assets assets, Events events, Threads threads, WindowSettings window)
    {
        _assets = assets;
        _events = events;
        _threads = threads;
        // The default icon: the project's Icons/Default.icon, else the engine's.
        _projectIcon = assets.Load(assets.Default<Icon>()) is { } icon ? icon.Image : Handle<Texture>.None;

        SDL.SetAppMetadata(project.Name, "1.0.0", "com.konfus.magic");
        SDL.SetLogOutputFunction(_logOutput, 0);
        if (!SDL.Init(SDL.InitFlags.Video | SDL.InitFlags.Gamepad))
            throw new InvalidOperationException($"SDL initialization failed: {SDL.GetError()}");

        int version = SDL.GetVersion(); // major * 1000000 + minor * 1000 + micro
        Log.Verbose($"SDL {version / 1000000}.{version / 1000 % 1000}.{version % 1000} initialised on thread {Environment.CurrentManagedThreadId}.");

        // Gamepads already plugged in are opened now, so the first frame has them (their queued "added" events then
        // find them open).
        foreach (uint id in SDL.GetGamepads(out _) ?? [])
            OnGamepadAdded(id);

        if (window.Open)
            OpenMainWindow(project.Name, window);
    }

    public void Dispose()
    {
        CloseGamepads();
        CloseAll();
        SDL.Quit();
        SDL.SetLogOutputFunction(SDL.GetDefaultLogOutputFunction(), 0);
    }

    /// <summary>
    /// Every frame on the render thread: SDL wants the OS message queue pumped from the thread that created the
    /// windows, and Windows flags a window as unresponsive if it is not. Each event updates the windows and the live
    /// input as it is taken off the queue; then what arrived becomes the input every reader sees, the next frame from
    /// its very start.
    /// </summary>
    public void Render(in Frame frame)
    {
        while (SDL.PollEvent(out SDL.Event e))
        {
            OnWindowEvent(ref e);
            OnInputEvent(ref e);
        }

        LatchInput();
    }

    private static void OnLog(nint userdata, SDL.LogCategory category, SDL.LogPriority priority, string message)
    {
        switch (priority)
        {
            case SDL.LogPriority.Critical:
            case SDL.LogPriority.Error:
                Log.Error($"SDL: {message}");
                break;
            case SDL.LogPriority.Warn:
                Log.Warn($"SDL: {message}");
                break;
            case SDL.LogPriority.Info:
                Log.Info($"SDL: {message}");
                break;
            default:
                Log.Debug($"SDL: {message}");
                break;
        }
    }
}
