using DryIoc;
using Magic.Interfaces;
using Magic.Services;
using Magic.Utils;
using Magic.Workers;

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

// TODO: Setup command parsing from console input and startup commands from cmd line

// Open the main window
IWindowFactory windowFactory = container.Resolve<IWindowFactory>();
IWindow window = windowFactory.Create("Magic Engine", 800, 600, WindowMode.Windowed);
window.Show();

// Main loop, on the main thread: systems run every frame.
SystemRegistry systems = container.Resolve<SystemRegistry>();
while (window.IsOpen)
{
    // TODO: delta time
    systems.Run(UpdateType.Update, 0);
    systems.Run(UpdateType.FixedUpdate, 0);
    systems.Run(UpdateType.LateUpdate, 0);
}

// Unload gems in reverse order: logger gems go last, so everything above them gets logged.
gemManager.UnloadAll();
Log.Flush();
