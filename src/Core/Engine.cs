using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Magic.Contexts.Assets;
using Magic.Contexts.CmdLine;
using Magic.Contexts.ECS;
using Magic.Contexts.Utils;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Internal;
using Magic.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Magic;

/// <summary>
/// Runs a project: opens it, makes its gems and its <see cref="World"/>, opens the project's entry point in the world,
/// runs frames until something stops them, and ends the gems. That is all Core does; everything else is a gem. The
/// frames run on two threads (<see cref="FrameLoop"/>), and are measured on the meter <c>Magic.Core</c>, which any gem
/// can listen to.
/// </summary>
public static class Engine
{
    /// <summary>
    /// Runs the project <paramref name="args"/> names. The calling thread must be the process's first: it becomes the
    /// render thread, which the OS ties windows and their events to, and makes and ends the gems; the frames' game side
    /// runs on a main thread started beside it. Returns once a gem quits the app or its frame or time limit is reached,
    /// or false straight away when the project cannot be opened (the reason is logged). Whatever fails after that is
    /// logged, and the run goes on without it.
    /// <para>
    /// Core's services, the world among them, and every gem and the interfaces each provides, are registered with a
    /// service provider, which makes each gem after whatever its constructor asks for (<see cref="GemLoader"/>). The
    /// assets are made before any gem loads, take each one's folder as it does, and are indexed once they all have. The
    /// settings are made next, as every gem's are made from the presets they read.
    /// </para>
    /// <para>
    /// A headless run has no window to close, so Ctrl+C in the terminal stops it cleanly, ending its gems. Otherwise
    /// Ctrl+C ends the process at once, as it does any program.
    /// </para>
    /// </summary>
    public static bool Run(Args args)
    {
        FileSystem files = new();
        Result<string> found = FindProject(files, args.ProjectPath);
        Result<Project> opened = found.Ok ? ReadProject(files, found.Value) : Result.Failure<Project>(found.Error);
        if (!found.Ok || !opened.Ok)
        {
            Debugging.Error(opened.Error);

            return false;
        }

        Project project = opened.Value;
        string folder = files.GetParent(found.Value);

        // A run that restarts runs again at once, with the command line, the world and the frame clock it ended with.
        (Args Args, WorldState? State, double Seconds)? next = (args, null, 0);
        while (next is { } run)
        {
            next = Run(files, project, folder, run.Args, run.State, run.Seconds);
        }

        return true;
    }

    /// <summary>
    /// Runs <paramref name="project"/>, in <paramref name="folder"/>, once, until the run ends, going on from
    /// <paramref name="state"/>, the world as the run before left it, and <paramref name="seconds"/>, its frame clock,
    /// when there is one: what to run it again with at once when the run restarts (<see cref="App.Restart"/>), the
    /// command line with every setting as it is now, the world as it is and its frame clock, else null.
    /// </summary>
    [SuppressMessage(
        "Usage",
        "VSTHRD002:Avoid problematic synchronous waits",
        Justification = "The assets' index is waited for before the run's threads start, and the saved world once "
            + "they have both ended; the index is read on workers, and the world's main-thread part ran before the "
            + "threads ended and the rest is written on a worker, so neither wait needs a thread it holds up.")]
    private static (Args Args, WorldState? State, double Seconds)? Run(
        FileSystem files,
        Project project,
        string folder,
        Args args,
        WorldState? state,
        double seconds)
    {
        using App app = new(project, folder);
        using Threads threads = new(app);

        // Headless, Ctrl+C quits the app, and its gems end; with a window it ends the process at once, as in any
        // program.
        using PosixSignalRegistration? interrupt = args.Headless
            ? PosixSignalRegistration.Create(
                PosixSignal.SIGINT,
                context =>
                {
                    context.Cancel = true;
                    app.Quit("Ctrl+C");
                })
            : null;

        Debugging.Info($"Running {project.Name} from {folder}");
        Events events = new();
        Assemblies assemblies = new();
        using Assets assets = new(files, app, events);
        ServiceCollection services = CreateCoreServices(
            files,
            app,
            threads,
            events,
            args,
            assemblies,
            assets);
        using GemLoader loader = new(services, files, project, assemblies, assets);

        // Not disposed: the gems are ended below one at a time, so one that throws cannot stop the rest ending.
        ServiceProvider container = services.BuildServiceProvider();
        assets.Services = container;

        // The settings and the gems find assets as they are made, so every folder is indexed first: each has been read
        // on a worker since it was added, as the gems loaded.
        assets.IndexAsync().GetAwaiter().GetResult();
        Settings settings = container.GetRequiredService<Settings>();
        List<IGem> gems = loader.Load(container);
        World world = container.GetRequiredService<World>();
        List<IPreloader> preloaders = [];
        foreach (IGem gem in gems)
        {
            if (gem is IPreloader preloader) preloaders.Add(preloader);
        }

        world.Preloaders = preloaders;

        using FrameLoop frames = new(
            gems,
            world,
            app,
            threads,
            events,
            settings,
            args.FrameLimit,
            args.TimeLimit,
            seconds);
        (Args Args, Task<WorldState> Saving, double Seconds)? next = null;
        threads.Run(
            () =>
            {
                // On the main thread before the first frame, once every gem is made, so every gem's loaders are there
                // to read the domain: the world as the run before left it, else the project's entry point, or, baking,
                // each of the project's domains in turn.
                if (state is { } saved) world.Restore(saved);
                else if (args.Bake) world.BakeAsync().FireAndForget();
                else world.OpenEntryPointAsync().FireAndForget();

                frames.Run();

                // On the main thread, the world's, once the last frame is done: the save takes the world here and now,
                // before what the ending run has handed this thread, a cancelled open closing its domain among it,
                // and before the gems end the world's scripts. The JSON is then written on a worker from what was
                // taken, so it may outlast the gems. Quitting is cancelled already, as the run is ending, so the
                // save runs on no token.
                if (app.IsRestarting)
                    next = (settings.RestartArgs(), world.SaveAsync(null, CancellationToken.None), frames.Seconds);

                // Still on the main thread: each gem ends what it owns of this thread's, the scripts among it, after
                // the save took them and before the gems are disposed on the render thread.
                frames.Stop();
            },
            () =>
            {
                Debugging.Frame = null;
                DisposeGems(gems);
                world.Dispose();
                settings.Dispose();
            });

        // Both engine threads have ended, so waiting here holds up no work of theirs.
        return next is { } restart ? (restart.Args, restart.Saving.GetAwaiter().GetResult(), restart.Seconds) : null;
    }

    /// <summary>
    /// Reads the project's .magic file at <paramref name="file"/>.
    /// </summary>
    private static Result<Project> ReadProject(FileSystem files, string file)
    {
        Result<byte[]> bytes = files.Read(file);

        if (!bytes.Ok) return Result.Failure<Project>(bytes.Error);

        Result<Project> read = Json.Read<Project>(bytes.Value);

        return read.Ok ? read : Result.Failure<Project>($"{file} is not a valid project: {read.Error}");
    }

    /// <summary>
    /// The project's .magic file at <paramref name="path"/>: that file, or the one in the folder there, which must hold
    /// exactly one.
    /// </summary>
    private static Result<string> FindProject(FileSystem files, string path)
    {
        if (files.FileExists(path)) return Result.Success(path);

        if (!files.FolderExists(path))
            return Result.Failure<string>($"There is no project at {path}: it is neither a .magic file nor a folder.");

        IReadOnlyList<string> found = files.Find(path, ".magic");

        if (found.Count != 1)
        {
            return Result.Failure<string>(
                $"The project folder {path} must hold exactly one .magic file; it holds {found.Count}.");
        }

        return Result.Success(found[0]);
    }

    /// <summary>
    /// What a gem's constructor can ask for of Core, before what the gems provide: the settings and the cache are made
    /// once every gem's assembly has been loaded. The world is made once a gem asks for it, or once every gem is, over
    /// the ECS and the scripts the gems provide, when they do. The code loaded, <paramref name="assemblies"/>, is a
    /// service too: the gem that reads <c>.cs</c> files finds a script's class in it. So are the
    /// <paramref name="assets"/>, made before the gems load, as each one's folder is added to them as it does.
    /// </summary>
    private static ServiceCollection CreateCoreServices(
        IFileSystem files,
        App app,
        Threads threads,
        Events events,
        Args args,
        Assemblies assemblies,
        Assets assets)
    {
        ServiceCollection services = new();
        services.AddSingleton(app);
        services.AddSingleton(assemblies);
        services.AddSingleton(assets);
        services.AddSingleton(files);
        services.AddSingleton(threads);
        services.AddSingleton(events);
        services.AddSingleton(args);
        services.AddSingleton(_ => new Settings(assets, files, events, app, args));
        services.AddSingleton(provider => new Caches(files, provider, app));
        services.AddSingleton(provider => new World(
            provider.GetService<IEcs>(),
            provider.GetService<IScripts>(),
            assemblies,
            assets,
            threads,
            events,
            app));

        return services;
    }

    /// <summary>
    /// Disposes the gems that are <see cref="IDisposable"/>, the last made first, so each ends before what it uses. A
    /// gem that is an <see cref="ILogSink"/> is taken off the log first, once it has had every line logged before, so
    /// it can close what it writes to.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Whatever one gem throws while ending, the others still end.")]
    private static void DisposeGems(List<IGem> gems)
    {
        for (int i = gems.Count - 1; i >= 0; i--)
        {
            if (gems[i] is ILogSink sink) Debugging.Unregister(sink);

            if (gems[i] is not IDisposable disposable) continue;

            try
            {
                disposable.Dispose();
            }
            catch (Exception ex)
            {
                Debugging.Error($"Gem {gems[i].GetType().Name} threw while disposing: {ex}");
            }
        }
    }
}
