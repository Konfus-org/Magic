using Magic.Contexts;
using Magic.Interfaces;
using Magic.Services;
using Magic.Utils;

namespace DefaultCheats;
/// <summary>
/// The gem: the one class in this dll that implements IGem. Constructor parameters are its dependencies: host
/// services (Project, Assets, Events, IFileSystem, Scheduler, World) or interfaces other gems provide, such as IEcs from the ECS gem.
/// The host loads gems in dependency order, so they are always there. To offer a service to the host and other gems,
/// implement its Core interface on this class (IAssetLoader&lt;T&gt;, IOverlay, ...). Name, static and dependencies
/// on other gems by name are set in the csproj (GemStatic, GemDependsOn).
/// </summary>
internal sealed class DefaultCheats : IGem
{
    private readonly IRendering _rendering;
    private readonly IWindowRegistry _windows;
    private readonly IFileSystem _files;
    private readonly Project _project;
    private readonly Threads _threads;
    private readonly World _world;
    private readonly Events _events;
    private readonly IEcs _ecs;
    private readonly IDisposable[] _cheatRegistrations;

    public DefaultCheats(IRendering rendering, IWindowRegistry windows, IFileSystem files, World world, Project project, Threads threads, Events events, IEcs ecs)
    {
        _world = world;
        _events = events;
        _ecs = ecs;
        _threads = threads;
        _rendering = rendering;
        _windows = windows;
        _files = files;
        _project = project;
        _cheatRegistrations =
        [
            Debugging.Commands.Register("screenshot", _ => Screenshot()),
            Debugging.Commands.Register("restore", Restore),
            Debugging.Commands.Register("exit", _ => world.End()),
        ];
    }

    public void Dispose()
    {
        foreach (IDisposable registration in _cheatRegistrations)
            registration.Dispose();
    }

    /// <summary>
    /// The main window as it was last shown, with the world's state in it, to <see cref="Project.Screenshots"/>, named by the time.
    /// </summary>
    private void Screenshot()
    {
        if (_windows.Main is not { } window)
        {
            Debugging.Log.Warn("Screenshot skipped: there is no main window.");
            return;
        }

        string path = _files.Combine(Project.Screenshots, $"{_project.Name}_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        Result taken = _threads.Invoke(ThreadId.Render, () => Debugging.Screenshot.Capture(_files, _rendering, window, _world, _ecs, path)); // a console command runs on the main thread
        if (taken.Failed)
        {
            Debugging.Log.Warn($"Screenshot failed: {taken.Message}");
            return;
        }

        Debugging.Log.Info($"Screenshot: {path}.");
    }

    /// <summary>
    /// <c>restore shot.png</c>: the world as it was when that screenshot was taken. A path that is not a file is
    /// looked for in <see cref="Project.Screenshots"/>.
    /// </summary>
    private void Restore(string[] args)
    {
        if (args.Length == 0)
        {
            Debugging.Log.Warn("Usage: restore <screenshot.png>");
            return;
        }

        string path = _files.FileExists(args[0]) ? args[0] : _files.Combine(Project.Screenshots, args[0]);
        Result restored = Debugging.Screenshot.Restore(_files, _world, _events, _ecs, path);
        if (restored.Failed)
        {
            Debugging.Log.Warn($"Restore failed: {restored.Message}");
            return;
        }

        Debugging.Log.Info($"Restored from {path}.");
    }
}
