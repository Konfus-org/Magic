using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Magic.Contexts.Assets;
using Magic.Contexts.CmdLine;
using Magic.Contexts.ECS;
using Magic.Contexts.Subscriptions;
using Magic.Contexts.Threading;
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
/// frames run on two threads (<see cref="FrameLoop"/>), and are measured on the meter <c>Magic</c>, which any gem can
/// listen to.
/// </summary>
public static class Engine
{
    /// <summary>
    /// Runs the project <paramref name="args"/> names. The calling thread must be the process's first: it becomes the
    /// render thread, which the OS ties windows and their events to, and makes and ends the gems; the frames' game side
    /// runs on a main thread started beside it. Returns once a gem stops the run or the frame limit is reached, or
    /// false straight away when the project cannot be opened (the reason is logged). Whatever fails after that is
    /// logged, and the run goes on without it.
    /// <para>
    /// Core's services, the world among them, and every gem and the interfaces each provides, are registered with a
    /// service provider, which makes each gem after whatever its constructor asks for (<see cref="GemLoader"/>). The
    /// settings are made first, as every gem's are made from the presets they read.
    /// </para>
    /// <para>
    /// A headless run has no window to close, so Ctrl+C in the terminal stops it cleanly, ending its gems. Otherwise
    /// Ctrl+C ends the process at once, as it does any program.
    /// </para>
    /// </summary>
    public static bool Run(Args args)
    {
        FileSystem files = new();
        Result<Project> opened = Project.Open(files, args.ProjectPath);
        if (!opened.Ok)
        {
            Debugging.Error(opened.Error);
            return false;
        }

        Project project = opened.Value;
        (Args Args, WorldState? State)? next = (args, null);
        while (next is { } run)
        {
            next = RunOnce(files, project, run.Args, run.State);
        }

        return true;
    }

    /// <summary>
    /// Runs <paramref name="project"/> once, until the run ends, going on from <paramref name="state"/>, the world as
    /// the run before left it, when there is one: what to run it again with at once when the run restarts
    /// (<see cref="Lifetime.Restart"/>), the command line with every setting as it is now and the world as it is, else
    /// null.
    /// </summary>
    private static (Args Args, WorldState? State)? RunOnce(FileSystem files, Project project, Args args, WorldState? state)
    {
        using Lifetime lifetime = new();
        using Threads threads = new(lifetime);
        using PosixSignalRegistration? interrupt = args.Headless
            ? PosixSignalRegistration.Create(PosixSignal.SIGINT, context =>
            {
                context.Cancel = true;
                lifetime.Stop("Ctrl+C");
            })
            : null;

        Debugging.Info($"Running {project.Name} from {project.Root}");
        Events events = new();
        Assemblies assemblies = new();
        ServiceCollection services = CreateCoreServices(files, lifetime, threads, events, project, args, assemblies);
        GemLoader loader = new(services, files, project, assemblies);

        // Not disposed: the gems are ended below one at a time, so one that throws cannot stop the rest ending.
        ServiceProvider container = services.BuildServiceProvider();
        Settings settings = container.GetRequiredService<Settings>();
        List<IGem> gems = loader.Make(container);
        World world = container.GetRequiredService<World>();
        using Subscription codeBuilds = loader.WatchCode(container.GetRequiredService<Assets>());

        // On the first frame, once every gem is made, so every gem's loaders are there to read the domain: the world
        // as the run before left it, or the project's entry point.
        threads.Post(EngineThread.Main, () => (state is null ? world.OpenEntryPointAsync() : world.RestoreAsync(state, lifetime.Stopping)).FireAndForget());

        using FrameLoop frames = new(gems, lifetime, threads, events, settings, args.FrameLimit, state?.Seconds ?? 0);
        (Args, WorldState?)? next = null;
        threads.Run(
            () =>
            {
                frames.Run();

                // On the main thread, the world's, once the last frame is done and before the gems end its scripts.
                if (lifetime.IsRestarting)
                {
                    next = (settings.RestartArgs(args), world.Save(frames.Seconds));
                }
            },
            () =>
            {
                Debugging.Frame = null;
                DisposeGems(gems);
                world.Dispose();
                settings.Dispose();
            });

        return next;
    }

    /// <summary>
    /// What a gem's constructor can ask for of Core, before what the gems provide: the assets, the settings and the
    /// cache are made once every gem's assembly, and so where its assets are, has been loaded. The world is made once
    /// a gem asks for it, or once every gem is, over the ECS and the scripts the gems provide, when they do. The code
    /// loaded, <paramref name="assemblies"/>, is a service too: the gem that reads <c>.cs</c> files finds a script's
    /// class in it.
    /// </summary>
    private static ServiceCollection CreateCoreServices(IFileSystem files, Lifetime lifetime, Threads threads, Events events, Project project, Args args, Assemblies assemblies)
    {
        ServiceCollection services = new();
        services.AddSingleton(assemblies);
        services.AddSingleton(files);
        services.AddSingleton(lifetime);
        services.AddSingleton(threads);
        services.AddSingleton(events);
        services.AddSingleton(project);
        services.AddSingleton(args);
        services.AddSingleton(provider => new Assets(files, project, lifetime, events, provider));
        services.AddSingleton(provider => new Settings(provider.GetRequiredService<Assets>(), files, events, project, args, lifetime));
        services.AddSingleton(provider => new Caches(files, provider));
        services.AddSingleton(provider => new World(
            provider.GetService<IEcs>(),
            provider.GetService<IScripts>(),
            assemblies,
            provider.GetRequiredService<Assets>(),
            threads,
            events,
            project,
            lifetime));
        return services;
    }

    /// <summary>
    /// Disposes the gems that are <see cref="IDisposable"/>, the last made first, so each ends before what it uses. A gem
    /// that is an <see cref="ILogSink"/> is taken off the log first, once it has had every line logged before, so it can
    /// close what it writes to.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Whatever one gem throws while ending, the others still end.")]
    private static void DisposeGems(List<IGem> gems)
    {
        for (int i = gems.Count - 1; i >= 0; i--)
        {
            if (gems[i] is ILogSink sink)
            {
                Debugging.Remove(sink);
            }

            if (gems[i] is not IDisposable disposable)
            {
                continue;
            }

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
