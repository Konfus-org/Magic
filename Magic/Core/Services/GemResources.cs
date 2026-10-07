using Magic.Utils;

namespace Magic.Services;

/// <summary>
/// The asset folders of the gems loaded: a gem's own <c>Resources</c>, which its build stamps into it (see
/// Directory.Build.props). The loader adds a gem's folder before constructing it and takes it away once the gem is
/// gone, so a gem finds its own assets from its constructor on. Main thread.
/// </summary>
public sealed class GemResources
{
    private readonly List<string> _folders = [];
    private readonly List<(Action<string> Added, Action<string> Removed)> _watchers = [];

    /// <summary>
    /// Calls <paramref name="added"/> for every folder there is now, then for each one that comes, and
    /// <paramref name="removed"/> for each one that goes, until the handle is disposed.
    /// </summary>
    public Subscription Watch(Action<string> added, Action<string> removed)
    {
        (Action<string>, Action<string>) watcher = (added, removed);
        _watchers.Add(watcher);
        foreach (string folder in _folders)
            added(folder);

        return new Subscription(() => _watchers.Remove(watcher));
    }

    internal void Add(string folder)
    {
        _folders.Add(folder);
        foreach ((Action<string> added, _) in _watchers.ToArray())
            added(folder);
    }

    internal void Remove(string folder)
    {
        if (!_folders.Remove(folder))
            return;

        foreach ((_, Action<string> removed) in _watchers.ToArray())
            removed(folder);
    }
}
