using System.Linq;
using Magic.Assets.Pipeline;
using Magic.Files;
using Magic.Pipelines;
using Magic.Runtime;
using Magic.Threading;
using Magic.Worlds;
using Microsoft.Extensions.DependencyInjection;

namespace Magic.GemAPI;

/// <summary>
/// The gems' side of a run, first to last: finds the ones the project lists, has what their constructors need made ready,
/// makes them and runs <see cref="Startup"/>; at the end runs <see cref="Shutdown"/> and unloads them. The services are
/// pumped while either is awaited, as no frames run. The gem that provides <see cref="IWindow"/> is the app's window for the
/// run.
/// </summary>
internal sealed class GemPipeline(
    App app,
    ServiceCollection services,
    IFileSystem files,
    AssetDatabase assets,
    World world,
    PipelineCoordinator pipelines
) : IPipeline<AppStart>, IPipeline<AppEnd>
{
    private IReadOnlyList<LoadedGem> _gems = [];

    public ValueTask<AppStart> RunAsync(AppStart state, CancellationToken cancel)
    {
        GemRegistry registry = new(services, files, state.Project.Gems);
        services.AddSingleton(
            new Assemblies([typeof(App).Assembly, .. registry.Gems.Select(gem => gem.Type.Assembly)])
        );

        // After the gems, as the world proxies what one of them provides, which would look like a clash.
        services.AddSingleton(world);
        pipelines.Get<GemsFound>().Run(new GemsFound(registry), cancel);

        // Never disposed, since its singletons are the gems, which AppEnd unloads.
        services.MakeReadOnly();
        IServiceProvider provider = services.BuildServiceProvider();
        _gems = GemLoader.Load(provider, registry, assets, pipelines, world);
        app.Window = GetWindow(provider);

        PumpUntil(
            pipelines.Get<Startup>().RunOnMainAsync(new Startup(state.Project), null, cancel).AsTask(),
            state.Stopping
        );

        return ValueTask.FromResult(state);
    }

    public ValueTask<AppEnd> RunAsync(AppEnd state, CancellationToken cancel)
    {
        PumpUntil(
            pipelines.Get<Shutdown>().RunOnMainInReverseAsync(new Shutdown(state.IsExiting), null, default).AsTask(),
            state.Stopping
        );

        // The window is one of the gems, which can't unload while the app holds it.
        app.Window = null;
        GemLoader.Unload(_gems);

        return ValueTask.FromResult(state);
    }

    /// <summary>
    /// The window the gems provide; none when no gem does, or the one that does wasn't made.
    /// </summary>
    private static IWindow? GetWindow(IServiceProvider provider)
    {
        try
        {
            return provider.GetService<IWindow>();
        }
        catch (InvalidOperationException)
        {
            // The gem loader says why it wasn't made.
            return null;
        }
    }

    /// <summary>
    /// Keeps the services going until <paramref name="running"/>, an awaited pipeline outside the frames, has ended, as what
    /// it awaits needs them, cancelling <paramref name="stopping"/> once the run is to end. The one wait on the main thread:
    /// no frame is due meanwhile, and this is what keeps the rest going.
    /// </summary>
    private void PumpUntil(Task running, CancellationTokenSource stopping)
    {
        running.FireAndForget();
        PipelineRunner<ServicePump> pump = pipelines.Get<ServicePump>();
        while (!running.IsCompleted)
        {
            pump.Run(default, stopping.Token);
            if (!app.IsRunning)
                stopping.Cancel();

            Thread.Sleep(1);
        }
    }
}
