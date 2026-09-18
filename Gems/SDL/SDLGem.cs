using Magic.Attributes;
using Magic.Services;
using SDL3;

namespace SDLGem;

[Gem("SDL", "1.0.0", "Provides SDL windowing and event handling.", Author = "Konfus")]
internal sealed class SDLGem : IDisposable
{
    private readonly IDisposable _eventSystem;

    public SDLGem(SystemRegistry systems)
    {
        if (!SDL.Init(SDL.InitFlags.Video))
            throw new InvalidOperationException($"SDL initialization failed: {SDL.GetError()}");

        // Runs every frame on the main thread: SDL wants the OS message queue pumped from the thread
        // that created the windows, and Windows flags a window as unresponsive if it isn't.
        _eventSystem = systems.Register(PumpEvents, UpdateType.Update);
    }

    private static void PumpEvents(float deltaTime)
    {
        while (SDL.PollEvent(out SDL.Event e))
        {
            switch ((SDL.EventType)e.Type)
            {
                case SDL.EventType.WindowCloseRequested:
                    WindowManager.OnCloseRequested(e.Window.WindowID);
                    break;
                case SDL.EventType.Quit:
                    WindowManager.CloseAll();
                    break;
            }
        }
    }

    public void Dispose()
    {
        _eventSystem.Dispose();
        SDL.Quit();
    }
}
