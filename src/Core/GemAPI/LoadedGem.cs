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
    /// <summary>
    /// The gem's load context, held so it lives as long as the gem is in use: nothing else keeps it alive, and once
    /// collected it would start unloading the gem while it still runs.
    /// </summary>
    private readonly AssemblyLoadContext? _context;
    private bool _isDisposed;

    internal LoadedGem(IGem gem, string name, Version version, string author, string description)
    {
        _context = AssemblyLoadContext.GetLoadContext(gem.GetType().Assembly);
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
        _context?.Unload();
    }
}
