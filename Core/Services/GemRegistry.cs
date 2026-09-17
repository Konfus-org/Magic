using Core.Contexts;
using System.Diagnostics.CodeAnalysis;

namespace Core.Services;

/// <summary>
/// Keeps track of all loaded gems and their associated metadata.
/// Provides methods for registering, unregistering, and querying gems.
/// </summary>
internal sealed class GemRegistry
{
    // Path to loaded gem
    private readonly Dictionary<string, GemContext> _pathToGem = [];

    public void Register(string path, GemContext gem)
    {
        _pathToGem[path] = gem;
    }

    public void Unregister(string path)
    {
        _pathToGem.Remove(path);
    }

    public bool TryGetValue(string path, [NotNullWhen(true)] out GemContext? gem)
    {
        return _pathToGem.TryGetValue(path, out gem);
    }
}
