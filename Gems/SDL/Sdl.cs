using Magic.Attributes;
using Magic.Services;
using Magic.Utils;
using SDL3;

namespace SDLGem;

/// <summary>
/// Owns SDL itself: <c>SDL_Init</c> on load, <c>SDL_Quit</c> on unload, the event pump every frame, and SDL's
/// log output routed into <see cref="Debugging"/>. Exports nothing; gems built on SDL (windowing, image, ttf) name
/// this one in <see cref="GemAttribute.DependsOn"/> and initialise only the subsystems they use, so that no
/// single one of them can quit SDL under the others. Events are not handed out: a gem that wants them registers
/// an <c>SDL.AddEventWatch</c> callback, which SDL runs from this gem's pump for every event as it is queued.
/// </summary>
[Gem(name: "SDL", version: "1.0.0", description: "Initialises SDL, pumps its events and routes its log output.", author: "Konfus", isStatic: true)]
internal sealed class Sdl : IDisposable
{
    // Kept in a field: SDL holds the native function pointer, and a collected delegate would crash the next log call.
    private readonly SDL.LogOutputFunction _logOutput = OnLog;
    private readonly IDisposable _pump;

    public Sdl(Project project, SystemScheduler systems)
    {
        SDL.SetAppMetadata(project.Name, "1.0.0", "com.konfus.magic");
        SDL.SetLogOutputFunction(_logOutput, IntPtr.Zero);
        if (!SDL.Init(0)) // core only; every subsystem is initialised by the gem using it
            throw new InvalidOperationException($"SDL initialization failed: {SDL.GetError()}");
        int version = SDL.GetVersion(); // major * 1000000 + minor * 1000 + micro
        Debugging.LogInfo($"SDL {version / 1000000}.{version / 1000 % 1000}.{version % 1000} initialised on thread {Environment.CurrentManagedThreadId}.");

        // Every frame on the main thread: SDL wants the OS message queue pumped from the thread that created
        // the windows, and Windows flags a window as unresponsive if it is not. Event watches see each event
        // as it enters the queue; nothing here looks at them, so the queue is simply drained.
        _pump = systems.Schedule(_ =>
        {
            while (SDL.PollEvent(out SDL.Event _)) { }
        }, UpdateType.Update);
    }

    public void Dispose()
    {
        _pump.Dispose();
        SDL.Quit();
        SDL.SetLogOutputFunction(SDL.GetDefaultLogOutputFunction(), IntPtr.Zero);
    }

    private static void OnLog(IntPtr userdata, SDL.LogCategory category, SDL.LogPriority priority, string message)
    {
        switch (priority)
        {
            case SDL.LogPriority.Critical:
            case SDL.LogPriority.Error:
                Debugging.LogError($"SDL: {message}");
                break;
            case SDL.LogPriority.Warn:
                Debugging.LogWarning($"SDL: {message}");
                break;
            case SDL.LogPriority.Info:
                Debugging.LogInfo($"SDL: {message}");
                break;
            default:
                Debugging.LogDebug($"SDL: {message}");
                break;
        }
    }
}
