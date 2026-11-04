using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Magic.Debugging;
using Magic.Ecs;
using Magic.Pipelines;
using Magic.Platform;
using Magic.Utils;
using Magic.Worlds;
using Microsoft.Extensions.DependencyInjection;

namespace Magic.Gems;

/// <summary>
/// The gems' side of a run: makes the gems the registry found, each after the gems it needs, runs each frame's hook over
/// them, and restarts the run once a build of one has settled. A gem that can't be made is logged, and the others go on
/// without it; one that throws in a hook is logged and leaves every hook and pipeline, and the frame goes on.
/// </summary>
internal sealed class GemPipeline(
    GemRegistry registry,
    IServiceProvider provider,
    App app,
    World world,
    PipelineCoordinator pipelines
)
    : IPipeline<AppStart>,
        IPipeline<ServicePump>,
        IPipeline<Update>,
        IPipeline<FixedUpdate>,
        IPipeline<LateUpdate>,
        IPipeline<Render>
{
    /// <summary>
    /// How long a gem's files must have been left alone after a build wrote them.
    /// </summary>
    private static readonly TimeSpan _settleTime = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Makes the gems through the provider, which was built from the services they were registered with, and sets each
    /// up: the app's window for as long as it lasts when it provides one, the world's ECS, its place on the log when it
    /// is a sink, and the pipelines it joins, so they run in the order the gems are made.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Whatever a gem's constructor throws, it is logged and the gem is not made; the others go on."
    )]
    public ValueTask<AppStart> RunAsync(AppStart state, CancellationToken cancel)
    {
        foreach (GemInfo gem in Sort())
        {
            IGem made;
            try
            {
                made = (IGem)provider.GetRequiredService(gem.Type);
            }
            catch (Exception ex)
            {
                // A missing service, a gem it needs that wasn't made, or a gem that can't start says why in the message;
                // anything else is a bug in the gem, and its stack says where.
                string why = ex is InvalidOperationException ? ex.Message : ex.ToString();
                Log.Error($"Gem {gem.Name} was not made: {why}");
                continue;
            }

            if (made is IEcs ecs)
                world.Ecs = ecs;

            LoadedGem loaded = registry.Add(made, gem);

            // The gem is an IWindow in any mode, but a headless run has no window.
            if (made is IWindow window && !app.IsHeadless)
            {
                app.Window = window;

                // The window is the gem, which can't unload while the app holds it.
                loaded.Subscriptions.Add(new Subscription(() => app.Window = null));
            }

            if (made is ILogSink sink)
                loaded.Subscriptions.Add(Log.Register(sink));

            loaded.Subscriptions.Add(pipelines.Register(made));
            string what = gem.Description.Length > 0 ? $": {gem.Description}" : string.Empty;
            Log.Info($"Gem {gem.Name} {gem.Version.ToString(3)} by {gem.Author} started{what}");
        }

        return ValueTask.FromResult(state);
    }

    /// <summary>
    /// Restarts the run once a build has written a gem's files and then left them alone for a while: a build writes each
    /// gem and its dependencies in turn.
    /// </summary>
    public ValueTask<ServicePump> RunAsync(ServicePump state, CancellationToken cancel)
    {
        long built = registry.BuildTimestamp;
        if (built == 0 || Stopwatch.GetElapsedTime(built) < _settleTime || app.IsRestarting)
            return ValueTask.FromResult(state);

        Log.Info("A gem's code changed, so the run restarts.");
        app.Restart();

        return ValueTask.FromResult(state);
    }

    public ValueTask<Update> RunAsync(Update state, CancellationToken cancel)
    {
        Run(Hook.Update, state.Frame);

        return ValueTask.FromResult(state);
    }

    public ValueTask<FixedUpdate> RunAsync(FixedUpdate state, CancellationToken cancel)
    {
        Run(Hook.FixedUpdate, state.Frame);

        return ValueTask.FromResult(state);
    }

    public ValueTask<LateUpdate> RunAsync(LateUpdate state, CancellationToken cancel)
    {
        Run(Hook.LateUpdate, state.Frame);

        return ValueTask.FromResult(state);
    }

    public ValueTask<Render> RunAsync(Render state, CancellationToken cancel)
    {
        Run(Hook.Render, state.Frame);

        return ValueTask.FromResult(state);
    }

    /// <summary>
    /// Calls <paramref name="hook"/> on every gem still in the frame, in the order they were made.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Whatever a gem throws, it is logged and the gem leaves the frame; the others go on."
    )]
    private void Run(Hook hook, in Frame frame)
    {
        foreach (LoadedGem gem in registry.Loaded)
        {
            if (gem.HasLeft)
                continue;

            try
            {
                switch (hook)
                {
                    case Hook.Update:
                        gem.Gem.Update(in frame);
                        break;
                    case Hook.FixedUpdate:
                        gem.Gem.FixedUpdate(in frame);
                        break;
                    case Hook.LateUpdate:
                        gem.Gem.LateUpdate(in frame);
                        break;
                    case Hook.Render:
                        gem.Gem.Render(in frame);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(hook), hook, "There is no such hook.");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Gem {gem.Name} failed in {hook} and stops running: {ex}");
                gem.HasLeft = true;
                pipelines.Remove(gem.Gem);
            }
        }
    }

    /// <summary>
    /// The gems the registry found, each after the gems it needs. A gem in a cycle, or one that needs it, is logged and
    /// left out, since the provider hangs making a cycle.
    /// </summary>
    private List<GemInfo> Sort()
    {
        List<GemInfo> sorted = [];
        HashSet<string> placed = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> refused = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> visiting = new(StringComparer.OrdinalIgnoreCase);
        foreach (GemInfo gem in registry.Gems)
        {
            Place(gem, sorted, placed, refused, visiting);
        }

        return sorted;
    }

    /// <summary>
    /// Places <paramref name="gem"/> in <paramref name="sorted"/> after the gems it needs: whether it was.
    /// </summary>
    private bool Place(
        GemInfo gem,
        List<GemInfo> sorted,
        HashSet<string> placed,
        HashSet<string> refused,
        HashSet<string> visiting
    )
    {
        if (placed.Contains(gem.Name))
            return true;

        // A gem visited again before it is placed is in a cycle; the gem that needs it says so.
        if (refused.Contains(gem.Name) || !visiting.Add(gem.Name))
            return false;

        foreach (string name in gem.Needs)
        {
            if (registry.Find(name) is not { } need || Place(need, sorted, placed, refused, visiting))
                continue;

            string why = visiting.Contains(name) ? "which needs it: a cycle" : "which was not made";
            Log.Error($"Gem {gem.Name} was not made: it needs {name}, {why}.");
            visiting.Remove(gem.Name);
            refused.Add(gem.Name);
            return false;
        }

        visiting.Remove(gem.Name);
        placed.Add(gem.Name);
        sorted.Add(gem);
        return true;
    }

    /// <summary>
    /// A hook of <see cref="IGem"/>'s, as the frame's pipelines call it over the gems.
    /// </summary>
    private enum Hook : byte
    {
        Update,
        FixedUpdate,
        LateUpdate,
        Render,
    }
}
