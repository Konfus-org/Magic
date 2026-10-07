using Magic.Contexts;
using Magic.Contexts.Threading;

namespace Magic.Interfaces;

/// <summary>
/// A gem: the one class in a gem dll implementing this. Its constructor parameters are its dependencies (host
/// services such as <see cref="Services.Project"/> or <see cref="Services.Events"/>, or another gem's services), and
/// it is provided under its own type and every engine interface it implements besides this one. Its name is the
/// assembly name; <c>GemStatic</c> in its csproj says whether it ever reloads, and the gems whose types it builds on
/// are its project references, which load first. The host calls the hooks once a frame, gems in load order
/// (dependencies first); implement only the ones you need. <see cref="Update"/>, <see cref="FixedUpdate"/> and
/// <see cref="LateUpdate"/> run on the main thread; <see cref="Render"/> runs on the render thread, which is also
/// the one a gem is constructed and disposed on. Everything the gem took from a host service (an event
/// watch, an ECS query) it disposes in <see cref="IDisposable.Dispose"/>.
/// </summary>
public interface IGem : IDisposable
{
    /// <summary>
    /// Nothing to let go of by default; a gem that took handles implements Dispose itself.
    /// </summary>
    void IDisposable.Dispose()
    {
    }

    void Update(in Frame frame)
    {
    }

    /// <summary>
    /// Zero or more times a frame, with <see cref="Frame.Delta"/> the fixed step.
    /// </summary>
    void FixedUpdate(in Frame frame)
    {
    }

    void LateUpdate(in Frame frame)
    {
    }

    /// <summary>
    /// On the render thread (<see cref="ThreadId.Render"/>), which owns the GPU and the windows, while the
    /// main thread waits: the ECS can be read and written as in any hook, and <see cref="IRendering"/> and window
    /// calls are on their own thread. <see cref="Present"/> follows.
    /// </summary>
    void Render(in Frame frame)
    {
    }

    /// <summary>
    /// On the render thread after every gem's <see cref="Render"/>, while the main thread still waits, in reverse load
    /// order: a gem draws on top of what the gems it depends on drew, and the renderer, which they all depend on, comes
    /// last and hands the frame to the GPU (off the main thread's wait, so the next frame overlaps it).
    /// </summary>
    void Present(in Frame frame)
    {
    }

    /// <summary>
    /// Hot reload: called on the old instance before it goes; what it returns the rebuilt one gets in <see cref="Reloaded"/>.
    /// </summary>
    byte[] Reloading()
    {
        return [];
    }

    /// <summary>
    /// Hot reload: called on the rebuilt instance, once constructed, with what the old one returned from <see cref="Reloading"/>.
    /// </summary>
    void Reloaded(byte[] state)
    {
    }
}
