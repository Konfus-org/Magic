using Magic.Contexts;
using Magic.Interfaces;
using Magic.Services;
using Magic.Utils;
using SDL3;

namespace SDLGem;

/// <summary>
/// Owns SDL itself: <c>SDL_Init</c> on load, <c>SDL_Quit</c> on unload, the event pump every frame, and SDL's
/// log output routed into <see cref="Debugging"/>. Provides nothing; gems built on SDL (windowing, input, image,
/// ttf) name this one in their csproj's <c>GemDependsOn</c> and initialise only the subsystems they use, so that no
/// single one of them can quit SDL under the others. Loading first, its Update pumps before theirs. SDL's events are
/// not handed out: a gem that wants them registers an <c>SDL.AddEventWatch</c> callback, which SDL runs from this
/// gem's pump for every event as it is queued.
/// </summary>
internal sealed class Sdl : IGem
{
    // Kept in a field: SDL holds the native function pointer, and a collected delegate would crash the next log call.
    private readonly SDL.LogOutputFunction _logOutput = OnLog;

    public Sdl(Project project)
    {
        SDL.SetAppMetadata(project.Name, "1.0.0", "com.konfus.magic");
        SDL.SetLogOutputFunction(_logOutput, IntPtr.Zero);

        if (!SDL.Init(0)) // core only; every subsystem is initialised by the gem using it
            throw new InvalidOperationException($"SDL initialization failed: {SDL.GetError()}");

        int version = SDL.GetVersion(); // major * 1000000 + minor * 1000 + micro
        int major = version / 1000000, minor = version / 1000 % 1000, micro = version % 1000;
        Debugging.Log.Info($"SDL {major}.{minor}.{micro} initialised on thread {Environment.CurrentManagedThreadId}.");
    }

    /// <summary>
    /// Every frame on the main thread: SDL wants the OS message queue pumped from the thread that created the
    /// windows, and Windows flags a window as unresponsive if it is not. Event watches see each event as it enters
    /// the queue; nothing here looks at them, so the queue is simply drained.
    /// </summary>
    public void Update(in Frame frame)
    {
        while (SDL.PollEvent(out SDL.Event _))
        {
        }
    }

    private static void OnLog(IntPtr userdata, SDL.LogCategory category, SDL.LogPriority priority, string message)
    {
        switch (priority)
        {
            case SDL.LogPriority.Critical:
            case SDL.LogPriority.Error:
                Debugging.Log.Error($"SDL: {message}");
                break;
            case SDL.LogPriority.Warn:
                Debugging.Log.Warn($"SDL: {message}");
                break;
            case SDL.LogPriority.Info:
                Debugging.Log.Info($"SDL: {message}");
                break;
            default:
                Debugging.Log.Debug($"SDL: {message}");
                break;
        }
    }

    public void Dispose()
    {
        SDL.Quit();
        SDL.SetLogOutputFunction(SDL.GetDefaultLogOutputFunction(), IntPtr.Zero);
    }
}
