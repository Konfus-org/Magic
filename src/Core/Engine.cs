using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Magic.Contexts.Assets;
using Magic.Contexts.CmdLine;
using Magic.Contexts.Threading;
using Magic.Contexts.Utils;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Internal;
using Magic.Services;

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
        (List<IGem> gems, World? world, Settings settings) = GemLoader.Load(project, files, lifetime, threads, events, args);

        // On the first frame, once every gem is made, so every gem's loaders are there to read the domain.
        if (world is not null)
        {
            threads.Post(EngineThread.Main, () => world.OpenEntryPointAsync().FireAndForget());
        }

        using FrameLoop frames = new(gems, lifetime, threads, events, settings, args.FrameLimit);
        threads.Run(frames.Run, () =>
        {
            Debugging.Frame = null;
            DisposeGems(gems);
            world?.Dispose();
            settings.Dispose();
        });

        return true;
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
