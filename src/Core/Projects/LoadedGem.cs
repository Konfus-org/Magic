using System.Runtime.Loader;
using Magic.Utils;

namespace Magic.Projects;

/// <summary>
/// A gem as made, and what ends with it. Disposing it ends the gem: its subscriptions (its place on the log when it is a
/// sink, the asset types it reads, the pipelines it joined), then the gem itself, then its load context, which frees its
/// assemblies once nothing refers to the gem.
/// </summary>
internal sealed class LoadedGem : IDisposable
{
    private readonly AssemblyLoadContext _context;
    private bool _isDisposed;

    internal LoadedGem(IGem gem, GemInfo info)
    {
        _context = info.Context;
        Gem = gem;
        Name = info.Name;
    }

    public IGem Gem { get; }

    /// <summary>
    /// The gem's name, as the project lists it.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Whether the gem threw in a hook and left the frame for good: it is still disposed with the others.
    /// </summary>
    public bool HasLeft { get; set; }

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
        _context.Unload();
    }
}
