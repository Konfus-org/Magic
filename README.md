# Magic

A modern, plugin-driven game engine in C# where nothing is glued in, in the spirit of Our Machinery and Bevy.
`Magic` is the engine: a host that loads a project and its gems (plugin dlls) and runs the frame loop, a
standalone program that works with or without the editor. The ECS, windows and input, rendering and logging all
live in gems (`Magic/Gems`), the engine's plugins. `Wizard` is the editor: a separate application, completely
decoupled from Magic, that drives it from outside (`Tools/Wizard/TUTORIAL.md`), with plugins of its own.

Everything is cross-platform (Windows, Linux, macOS planned), modern .NET, and written for performance; the rules
are in the [code standards](CODESTANDARDS.md).

## Building and running

Needs the .NET 10 SDK. Configurations: `Debug`, `Optimize` (optimised, full symbols), `Release`.

```powershell
dotnet build Magic-Wizard.slnx
dotnet test Magic-Wizard.slnx
Build/net10.0/Debug/bin/Magic --project Samples
```

The solution builds every gem; `dotnet build Magic/Core/Core.csproj` alone builds the engine and its default gems.

The Samples project opens a hub with the console up: `portal` lists the samples, `portal DaytimeCity` opens one, `summon
Prop_Wagon.glb` puts a model in front of the camera ([Samples/README.md](Samples/README.md)). The terminal the host was started from is a
console too, so `--headless` runs are driven by typing there.

`Magic --help` lists the options. Exit codes: 0 clean, 1 bad arguments or a crash, 2 an error was logged under
`--fail-on-error`.

Windows and Linux (Vulkan) are supported; macOS is not yet. `Tools/build-all.ps1 -Runtime linux-x64` builds the
host, the gems and the samples for another platform into its own folder (`Build/net10.0/Debug-linux-x64/`); the
solution itself refuses a runtime. The host project's "Magic (WSL)" launch profile runs that build in WSL, with the
Samples project; run the script before it. It needs the ".NET Debugging with WSL" component of Visual Studio.

## How it works

Core (`Magic/Core`, built as `Magic.exe` and `Magic.dll`) is the runner and the engine's vocabulary: it loads the
project and its gems, hands every gem the same frame, holds the services every gem may ask for, and holds every type
more than one gem names (components, assets, events, settings, the interfaces gems implement). Everything else is a
gem (`Magic/Gems`): a dll with one class implementing `IGem`, which references Core only, so swapping one (a
renderer, the platform, the ECS) is one line in the project's `gems` list. The one exception is a gem that reads
another gem's public markers: DeferredRenderer and ChunkStreaming reference EntityHierarchy for its `Static`,
`Hidden` and `Settled`.

- **Host** (`Magic/Core/Program.cs`): reads the project's `.magic` file, builds Core's services (`Settings`,
  `Randomness`, `DebugUI`, `Assets` and `World` among them) before any gem, loads the gems, then opens the entry point
  (`World.OpenEntryPoint`), which may be a gem's asset. Each frame it applies the asset changes that settled
  (`Assets.ProcessChanges`) and hands one immutable `Frame` to every gem's `Update`, `FixedUpdate` (60 Hz),
  `LateUpdate` and `Render`, then `Present` on the render thread. The frame loop runs on a main thread; `Render`,
  `Present` and the GPU submit run on the render thread, which owns the windows.
- **Gems** (`Magic/Core/Internal/Gems.cs`): a gem's constructor parameters are its dependencies (a `T?` is optional,
  a `T[]` is every provider) and the interfaces it implements are what it provides. The gems it references load
  first, even when the project's `gems` list does not name them, unload after it, and reload it when they reload; a
  rebuilt dll of a gem that is not static (a project's) is reloaded in place. A gem's own assets are the `Resources` folder beside its csproj, added to the
  assets while it is loaded; Core's are there for the whole run. A project leaves a gem out with `"-Name"` in its
  `gems` list, or replaces it by shipping a gem of the same name.
- **Threads** (`Magic/Core/Services/Threads.cs`): `Post`/`Invoke`/`InvokeAsync(work, thread)`. No thread id means a
  worker of the .NET thread pool; `Main` and `Render` are the engine's two dedicated threads, and `MainThread` is the
  shorthand for the first. Work given to `InvokeAsync` is handed its `CancellationToken`, and never starts once that
  is cancelled.
- **Events** (`Magic/Core/Services/Events.cs`): any struct is an event, published from any thread and read in the next
  frame as `frame.Events.Of<T>()`, or handed to a `Watch<T>`.
- **Settings** (`Magic/Core/Services/Settings.cs`): Core's and every gem's `[Settings]` classes, set from the command
  line (`--set Name.Key=value`), a preset, or the settings window, live.
- **Assets** (`Magic/Core/Services/Assets`, [Magic/Core/Resources/README.md](Magic/Core/Resources/README.md)):
  addressed by id, not path; the id and type live in a `.meta` file beside each asset. Gems add formats with
  `IAssetLoader<T>`. `LoadAsync` reads and imports off the calling thread, stops for a `CancellationToken` and tells
  an `IProgress<float>` how far its dependencies are; `Load` waits for the same thing.
- **World** (`Magic/Core/Services/World`): a stack of open domains. A domain is a folder of `x_y_z.chunk` files
  streamed in around the cameras (`Magic/Gems/World/ChunkStreaming`). `World.Open` (or `OpenAsync`) loads the
  domain's own file and answers a `Result`; each open domain (`World.Domains`) has a `State`, `Loading` until its
  globals and the chunks its cameras want are spawned, and a `Progress` that goes on to the `IProgress<float>` given to
  `Open`; `World.State` is `Loading` while a domain opened in place of the world is not there yet, which is when the
  loading screen (`Magic/Gems/World/LoadingScreen`) shows its domain over it.
- **ECS** (`Magic/Core/Services/Ecs`, `Magic/Gems/World/FlecsEcs`): components are structs implementing `IComponent`;
  systems are `ISystem`s given to `IEcs.Add`, in a phase (`Update`, `FixedUpdate`, `LateUpdate`, `Render`, then
  `Overlay` for what draws on top of the scene). EntityHierarchy (`Magic/Gems/World/EntityHierarchy`) computes every
  entity's `WorldTransform` and keeps the `Static`, `Hidden` and `Settled` markers.
- **Scripts** (`Magic/Core/Contexts/Scripting`, `Magic/Gems/World/Scripting`): a chunk lists scripts on its entities;
  the Scripting gem runs them, whatever language they are written in, from what their `Script` asset says (made how,
  impatient, always updated). C# is one language (`Magic/Gems/CSharpScripting`): a `.cs` asset's class, an `ISystem`
  or an `IBehavior` compiled by the project's csproj, marked `[Impatient]` or `[AlwaysUpdate]` as needed.
- **Rendering** (`Magic/Gems/Rendering/DeferredRenderer` over `Magic/Gems/Rendering/SdlGpuRenderer`): GPU-driven on
  SDL_GPU, with the pipeline, its passes and the posts defined as data in its `Resources`.
- **Debugging**: `Log` writes to every logger gem; `ITelemetry` takes the numbers the debug display (F3) shows; a gem
  shows the developer text in the world and notes through `IDebugUI`, and adds console commands through
  `IConsoleCommands`.

## Folder structure

```
Magic/
  Core/        Magic.exe and Magic.dll: Program, the gem loader, the services every gem may ask for and every type
               gems share, by area (Assets, Debug, Ecs, Maths, Platform, Randomness, Rendering, Scripting, World);
               its Resources hold what every part may use: the primitive models, the default domain,
               materials, textures, icon and presets, and the generic shaders
  Gems/        The engine's gems
    Assets/      AssimpModelLoader, SdlImageLoader, SdlFontLoader
    Debug/       DebugTools, ImGuiDebugUI, Screenshots, ConsoleCheats, ZLoggerLogging
    LODs/        ModelLodGenerator, ChunkLodGenerator
    Rendering/   DeferredRenderer, SdlGpuRenderer, SdlShaderCompiler
    World/       FlecsEcs, EntityHierarchy, Scripting, ChunkStreaming, LoadingScreen
    SdlPlatform/ CSharpScripting/ SettingsPresets/
Samples/       The samples, one project (Samples/README.md)
Tools/         dotnet new templates for projects, gems and samples (Tools/VSTemplates/README.md)
Build/         All build output (not checked in)
```

Every part, Core and each gem, is laid out the same way: its csproj (named for its folder) and its `IGem` class at
the root (Core's `Program`), then `Contexts` (public data: components, assets, events, settings), `Services` (public
interfaces and the classes that do the work), `Internal` (everything not public), `Resources` (its assets) and
`Tests` (its unit tests, [TESTING.md](TESTING.md)). Core splits `Contexts`, `Services` and `Tests` by area. The
solution's folders mirror these.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) and the [code standards](CODESTANDARDS.md).

AI-assisted contributions are welcome under the same standards as any other code: you must
understand, be able to explain, and be ready to change what you submit, and a human reviews and approves
everything before it merges.
