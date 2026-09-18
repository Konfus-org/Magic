using DryIoc;
using Magic.Interfaces;
using Magic.Services;
using Magic.Utils;
using Magic.Workers;
using System.Diagnostics;

// Setup core services. The container stays open: gems register into it later.
// Reuse is explicit on purpose: the default (transient) is the safe one for anything a gem registers.
using Container container = new();
container.RegisterInstance<IContainer>(container);
container.Register<IFileOperations, FileOperations>(Reuse.Singleton);
container.RegisterInstance(new Directories(AppContext.BaseDirectory, "Gems"));
container.Register<GemLoader>(Reuse.Singleton);
container.Register<GemManager>(Reuse.Singleton);
container.Register<SystemRegistry>(Reuse.Singleton);

// Load gems (loggers first, then in dependency order) and watch the folder to hot reload them.
string gemsDirectory = container.Resolve<Directories>().Gems;
GemManager gemManager = container.Resolve<GemManager>();
using GemWatcher gemWatcher = new(gemsDirectory, gemManager);
await gemManager.LoadAllAsync(gemsDirectory).ConfigureAwait(false);

// TODO: Setup command parsing from console input and startup commands from cmd line using System.CommandLine
// TODO: ECS up next!
// TODO: Then asset pipeline! We need to be able to load assets to render them
// TODO: After assets we need rendering!

// Open the main window
IWindowFactory windowFactory = container.Resolve<IWindowFactory>();
IWindow window = windowFactory.Create("Magic Engine", 800, 600, WindowMode.Windowed);
window.Show();

// Main loop, on the main thread: systems run every frame.
SystemRegistry systems = container.Resolve<SystemRegistry>();

// Fixed timestep with an accumulator: Update and LateUpdate get the real frame time, FixedUpdate runs
// zero or more times per frame at a constant step so simulation stays deterministic regardless of frame rate.
// All times are in milliseconds.
const double fixedDeltaMs = 1000d / 60d;
const double maxFrameMs = 250d; // after a stall (breakpoint, window drag) don't try to catch up forever

Stopwatch clock = Stopwatch.StartNew();
double lastMs = 0;
double accumulatorMs = 0;
while (window.IsOpen)
{
    double nowMs = clock.Elapsed.TotalMilliseconds;
    double frameMs = Math.Min(nowMs - lastMs, maxFrameMs);
    lastMs = nowMs;
    accumulatorMs += frameMs;

    systems.Run(UpdateType.Update, frameMs);
    while (accumulatorMs >= fixedDeltaMs)
    {
        systems.Run(UpdateType.FixedUpdate, fixedDeltaMs);
        accumulatorMs -= fixedDeltaMs;
    }
    systems.Run(UpdateType.LateUpdate, frameMs);
}

// Unload gems in reverse order: logger gems go last, so everything above them gets logged.
gemManager.UnloadAll();
Log.Flush();
