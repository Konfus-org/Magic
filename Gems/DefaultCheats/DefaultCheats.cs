using Magic.Extensions;
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
    private readonly IDisposable[] _cheatRegistrations;

    public DefaultCheats(IRendering rendering, IWindowRegistry windows, IFileSystem files, World world, Project project)
    {
        _rendering = rendering;
        _windows = windows;
        _files = files;
        _project = project;
        _cheatRegistrations =
        [
            Debugging.Commands.Register("screenshot", _ => Screenshot()),
            Debugging.Commands.Register("exit", _ => world.End()),
        ];
    }

    public void Dispose()
    {
        foreach (IDisposable registration in _cheatRegistrations)
            registration.Dispose();
    }

    /// <summary>The main window as it was last shown, to <see cref="Project.Screenshots"/>, named by the time.</summary>
    private void Screenshot()
    {
        if (_windows.Main is not { } window)
        {
            Debugging.Log.Warn("Screenshot skipped: there is no main window.");
            return;
        }

        string path = _files.Combine(Project.Screenshots, $"{_project.Name}_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        Result taken = _rendering.Screenshot(window, _files, path);
        if (taken.Failed)
        {
            Debugging.Log.Warn($"Screenshot failed: {taken.Message}");
            return;
        }

        Debugging.Log.Info($"Screenshot: {path}.");
    }
}
