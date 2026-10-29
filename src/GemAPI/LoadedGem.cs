using System.Runtime.Loader;
using Magic.Utils;

namespace Magic.GemAPI;

/// <summary>
/// A gem as loaded, and what ends with it. Disposing it ends the gem: its subscriptions (its place on the log when it
/// is a sink, and whatever is registered for it later), then the gem itself, then its load context, which frees its
/// assemblies once nothing refers to the gem.
/// </summary>
public sealed class LoadedGem : IDisposable
{
    private bool _isDisposed;

    internal LoadedGem(IGem gem, string name, Version version, string author, string description)
    {
        Gem = gem;
        Name = name;
        Version = version;
        Author = author;
        Description = description;
    }

    public IGem Gem { get; }

    /// <summary>
    /// The gem's name, as the project lists it.
    /// </summary>
    public string Name { get; }

    public Version Version { get; }

    public string Author { get; }

    public string Description { get; }

    /// <summary>
    /// What ends with the gem; the host adds to it.
    /// </summary>
    internal Subscriptions Subscriptions { get; } = [];

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        Subscriptions.Dispose();
        (Gem as IDisposable)?.Dispose();
        AssemblyLoadContext.GetLoadContext(Gem.GetType().Assembly)?.Unload();
    }
}
