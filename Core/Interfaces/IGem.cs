using Magic.Contexts;

namespace Magic.Interfaces;

/// <summary>
/// A gem: the one class in a gem dll implementing this. Its constructor parameters are its dependencies (host
/// services such as <see cref="Services.Project"/> or <see cref="Services.Events"/>, or another gem's interfaces),
/// and every Core interface it implements besides this one is what it provides to the host and other gems. Its name
/// is the assembly name; <c>GemStatic</c> and <c>GemDependsOn</c> in its csproj say whether it ever reloads and which
/// gems it needs loaded without a service between them. The host calls the hooks once a frame, gems in load order
/// (dependencies first); implement only the ones you need. Everything the gem took from a host service (an event
/// watch, an ECS query) it disposes in <see cref="IDisposable.Dispose"/>.
/// </summary>
public interface IGem : IDisposable
{
    /// <summary>Nothing to let go of by default; a gem that took handles implements Dispose itself.</summary>
    void IDisposable.Dispose()
    {
    }

    void Update(in Frame frame)
    {
    }

    /// <summary>Zero or more times a frame, with <see cref="Frame.Delta"/> the fixed step.</summary>
    void FixedUpdate(in Frame frame)
    {
    }

    void LateUpdate(in Frame frame)
    {
    }

    void Render(in Frame frame)
    {
    }

    /// <summary>Hot reload: called on the old instance before it goes; the rebuilt one gets it back in <see cref="Restore"/>.</summary>
    byte[] Save()
    {
        return [];
    }

    void Restore(byte[] state)
    {
    }
}
