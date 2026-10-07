using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Contexts.Domain;
using Magic.Contexts.Rendering;
using Magic.Contexts.Threading;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Services;
using Magic.Utils;
using System.Text;

namespace ScreenshotsGem;

/// <summary>
/// The gem: the main window to <see cref="Project.Screenshots"/> as PNGs that carry the world's state. Taken on their
/// own by frame (<see cref="ScreenshotSettings"/>, which <c>--screenshots</c> sets), right after the frame reaches the
/// GPU, and on demand with the <c>screenshot</c> command; <c>restore</c> puts the world back as a screenshot shows it.
/// A screenshot carries which domains were open and where every named camera was, as a PNG text chunk any image viewer
/// ignores (<see cref="Capture"/>), and <see cref="RestoreWorld"/> reads that back out of the file and puts the world
/// there again.
/// </summary>
internal sealed class Screenshots : IGem
{
    /// <summary>
    /// The PNG text chunk the state is under.
    /// </summary>
    private const string Keyword = "Magic";

    private readonly IFileSystem _files;
    private readonly Project _project;
    private readonly World _world;
    private readonly Threads _threads;
    private readonly ScreenshotSettings _settings;
    private readonly IRendering? _rendering;
    private readonly IWindowRegistry? _windows;
    private readonly IEcs? _ecs;
    private readonly Subscription[] _commands;
    private int _taken;
    private long _due;

    public Screenshots(IFileSystem files, Project project, World world, Threads threads, ScreenshotSettings settings, IConsoleCommands commands, IRendering? rendering, IWindowRegistry? windows, IEcs? ecs)
    {
        _files = files;
        _project = project;
        _world = world;
        _threads = threads;
        _settings = settings;
        _rendering = rendering;
        _windows = windows;
        _ecs = ecs;
        _due = settings.Delay;
        _commands =
        [
            commands.Add("screenshot", _ => TakeNow()),
            commands.Add("restore", Restore),
        ];
    }

    public void Dispose()
    {
        foreach (Subscription command in _commands)
            command.Dispose();
    }

    /// <summary>
    /// A frame that is due is captured once the renderer has submitted it.
    /// </summary>
    public void Present(in Frame frame)
    {
        if (_taken >= _settings.Count || frame.Number < _due)
            return;

        _taken++;
        _due += Math.Max(1, _settings.Interval);
        long number = frame.Number;
        if (_rendering is not { } rendering || _windows?.Main is not { } window)
        {
            Log.Error($"Screenshot at frame {number} skipped: there is no renderer or no main window.");
            return;
        }

        string path = _files.Combine(_project.Screenshots, $"{_project.Name}_{number:D6}.png");
        rendering.AfterSubmit(() => Write(Capture(rendering, window, path), $"Screenshot at frame {number}", path, LogLevel.Error));
    }

    /// <summary>
    /// <c>screenshot</c>: the main window as it was last shown, named by the time. A console command runs on the main thread.
    /// </summary>
    private void TakeNow()
    {
        if (_rendering is not { } rendering || _windows?.Main is not { } window)
        {
            Log.Warn("Screenshot skipped: there is no renderer or no main window.");
            return;
        }

        string path = _files.Combine(_project.Screenshots, $"{_project.Name}_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        Result taken = _threads.Invoke(ThreadId.Render, () => Capture(rendering, window, path));
        Write(taken, "Screenshot", path, LogLevel.Warning);
    }

    /// <summary>
    /// <c>restore shot.png</c>: the world as it was when that screenshot was taken. A path that is not a file is
    /// looked for in <see cref="Project.Screenshots"/>.
    /// </summary>
    private void Restore(string[] args)
    {
        if (args.Length == 0)
        {
            Log.Warn("Usage: restore <screenshot.png>");
            return;
        }

        string path = _files.FileExists(args[0]) ? args[0] : _files.Combine(_project.Screenshots, args[0]);
        Result restored = RestoreWorld(path);
        if (restored.Failed)
        {
            Log.Warn($"Restore failed: {restored.Message}");
            return;
        }

        Log.Info($"Restored from {path}.");
    }

    /// <summary>
    /// What <paramref name="window"/> last showed, before anything drawn over it, as a PNG at <paramref name="path"/>
    /// that carries the world's state. Waits for the GPU. Failed, with why, when nothing has been shown in the window
    /// yet or the file could not be written. Without an ECS no camera is saved. Render thread.
    /// </summary>
    private Result Capture(IRendering rendering, IWindow window, string path)
    {
        Result<CapturedFrame> taken = rendering.Read(GpuTexture.Window(window.Handle));
        if (taken.Failed)
            return Result.Failure(taken.Message);

        State state = new()
        {
            Domains = WorldOf(_world),
            Cameras = _ecs is null ? [] : CamerasOf(_ecs),
        };

        CapturedFrame frame = taken.Payload;
        return _files.WriteBinary(path, frame.Pixels.AsSpan().Png(frame.Width, frame.Height, (Keyword, AssetJson.Format(state, typeof(State)))));
    }

    /// <summary>
    /// Puts the world back as it was when the screenshot at <paramref name="path"/> was taken: its domains are opened in
    /// place of whatever is open (they are left alone when they are the ones open already), and once they are loaded
    /// every camera saved is put where it was. Failed, with why, when the file cannot be read, carries no state or a
    /// domain cannot be opened. Without an ECS no camera is moved. Main thread.
    /// </summary>
    private Result RestoreWorld(string path)
    {
        Result<byte[]> read = _files.ReadBinary(path);
        if (read.Failed)
            return Result.Failure(read.Message);

        if (read.Payload.AsSpan().PngText(Keyword) is not { } json)
            return Result.Failure($"{path} carries no world state: it is not a screenshot this engine took.");

        Result<State> state = AssetJson.Read<State>(Encoding.UTF8.GetBytes(json));
        if (state.Failed)
            return Result.Failure($"The world state in {path} cannot be read: {state.Message}");

        Handle<Domain>[] domains = state.Payload.Domains;
        if (!domains.SequenceEqual(WorldOf(_world)))
        {
            Result opened = _world.Open(domains.Length > 0 ? domains[0] : Handle<Domain>.None);
            for (int index = 1; index < domains.Length && opened.Ok; index++)
                opened = _world.Open(domains[index], OpenMode.Additive);

            if (opened.Failed)
                return opened;
        }

        if (_ecs is not { } ecs || state.Payload.Cameras.Length == 0)
            return Result.Success();

        // The cameras come with the domains: placed once the last of those is there.
        _world.WhenLoaded(domains, () => Place(ecs, state.Payload.Cameras));
        return Result.Success();
    }

    private static void Write(Result taken, string what, string path, LogLevel failure)
    {
        if (taken.Ok)
            Log.Info($"{what}: {path}.");
        else if (failure == LogLevel.Error)
            Log.Error($"{what} failed: {taken.Message}");
        else
            Log.Warn($"{what} failed: {taken.Message}");
    }

    /// <summary>
    /// The domains that make up the world, in the order they were opened: overlays (a loading screen) are not of it.
    /// </summary>
    private static Handle<Domain>[] WorldOf(World world)
    {
        return [.. world.Domains.Where(open => open.Mode != OpenMode.Overlay).Select(open => open.Handle)];
    }

    /// <summary>
    /// Every camera that can be found again, by its path; one with a nameless entity above it, or itself nameless, cannot.
    /// </summary>
    private static CameraState[] CamerasOf(IEcs ecs)
    {
        List<CameraState> cameras = [];
        using IEcsQuery<Transform, Camera> query = ecs.Query<Transform, Camera>().Build();
        query.Each((Handle entity, ref Transform transform, ref Camera _) =>
        {
            if (PathOf(ecs, entity) is { } path)
                cameras.Add(new CameraState(path, transform));
            else
                Log.Debug($"Camera {entity} has no name, so the screenshot does not carry where it is.");
        });

        return [.. cameras];
    }

    /// <summary>
    /// The names from the root down to <paramref name="entity"/>, as <see cref="IEcs.Lookup"/> takes them; null when one is missing.
    /// </summary>
    private static string? PathOf(IEcs ecs, Handle entity)
    {
        string? path = null;
        for (Handle at = entity; at.IsValid; at = ecs.GetParent(at))
        {
            if (ecs.GetName(at) is not { } name)
                return null;

            path = path is null ? name : $"{name}.{path}";
        }

        return path;
    }

    private static void Place(IEcs ecs, CameraState[] cameras)
    {
        foreach (CameraState camera in cameras)
        {
            Handle entity = ecs.Lookup(camera.Path);
            if (!entity.IsValid || !ecs.Has<Camera>(entity))
            {
                Log.Warn($"Camera {camera.Path} of the screenshot is not in the world; it is not restored.");
                continue;
            }

            ecs.Set(entity, camera.Transform);
        }
    }

    /// <summary>
    /// What a screenshot carries, as JSON: the open domains in the order they were opened, and the cameras.
    /// </summary>
    private sealed class State
    {
        public Handle<Domain>[] Domains { get; set; } = [];

        public CameraState[] Cameras { get; set; } = [];
    }

    /// <summary>
    /// A camera by its entity's path, with its transform relative to its parent.
    /// </summary>
    private readonly record struct CameraState(string Path, Transform Transform);
}
