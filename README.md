# Magic

A small C# game engine. `Magic.exe` is a host that loads a project, loads gems (plugin dlls) into it, opens a
window and runs the frame loop. Almost everything beyond that, including the ECS, windowing, rendering and
logging, lives in a gem and can be hot reloaded while the engine runs.

## How it works

- **Host** (`Core/Program.cs`). Parses the command line, reads the project's `.magic` file, loads gems, opens
  the main window and runs a variable frame with a fixed 60 Hz step. Each frame builds one immutable `Frame`
  (number, time, delta, the events since the last frame) and hands it down in a fixed order: every gem's
  `Update`, the Core Update systems, `FixedUpdate` zero or more times, `LateUpdate`, then `Render` (see
  `Program.Step`).
- **Gems** (`Core/Gems.cs`). A gem is one dll with one class that implements `IGem`. Its constructor parameters
  are its dependencies, taken from the `Container` (host services and other gems' interfaces), which is also how
  load order is decided; the Core interfaces it implements are what it provides. `GemStatic` and `GemDependsOn`
  in its csproj are the only other things the host reads. Engine gems come from `bin/Gems/`, and the project
  lists the ones it wants. Every gem dll under the project folder loads too. A changed dll is unloaded and
  loaded again.
- **Events** (`Core/Services/Events.cs`). One `Event` struct with a `Type`, like `SDL_Event`. Anything publishes
  from any thread; the frame loop takes the queue once a frame into `Frame.Events`. Read them in a hook, or
  `Watch` a type and dispose the watch when done.
- **Assets** (`Core/Services/Assets.cs`). Assets are addressed by id, not path. Each asset file has a `.meta`
  file next to it that holds the id and the asset type. The manager keeps the index only: every load reads the
  file. Gems load other formats by implementing `IAssetLoader<T>`, and file changes are published as events.
- **ECS** (`Core/Interfaces/IEcs*.cs`, `Gems/FlecsEcs`). Components are structs that implement `IComponent`.
  A world is a folder of `x_y_z.chunk` files plus a `<Name>.world` file, and `StreamingSystem` loads and
  unloads chunks around the cameras.
- **Systems** (`Core/Systems`). The core systems are streaming, transform, overlay, render, debug UI and
  debugger display: plain classes the frame loop calls by name. Gems do their per-frame work in their `IGem`
  hooks, or schedule ECS systems with `IEcs.Schedule`.
- **Rendering** (`Gems/SDLRender`). A GPU-driven renderer on SDL_GPU. Culling, HiZ occlusion and materials
  are all driven by data in `Resources/Passes` and `Resources/Materials`.

## Folder structure

```
Core/               Magic.exe: host, gem loader, assets, ECS contracts, core systems
    Attributes/     [AssetFormat] and [MetaData]
    Contexts/       Plain data: Frame, Event, assets, ECS components, input enums, settings
    Extensions/     Extension methods
    Interfaces/     Contracts that gems implement or consume
    Mathematics/    Bounds, frustum, ray
    Services/       Host services: container, events, assets, file system, project
    Systems/        Core ECS systems
    Utils/          Debugging (log and immediate-mode UI), Result, ChangeQueue, Png
Gems/               Engine gems, built into Build/.../bin/Gems/
Resources/          Engine assets: shaders, materials, passes, models, textures
Samples/            Example projects; see Samples/README.md
Tests/              Unit and integration tests (xUnit) and TestGem; see Tests/README.md
Tools/              dotnet new templates for projects and gems; see Tools/VSTemplates/README.md
Build/              All build output, per framework and configuration (not checked in)
```

## Building and running

You need the .NET 10 SDK. The configurations are `Debug`, `Optimize` (optimised, with full symbols) and
`Release`.

```powershell
dotnet build Magic.slnx
dotnet test Tests\Magic.UnitTests
dotnet test Tests\Magic.IntegrationTests
Build\net10.0\Debug\bin\Magic.exe --project Samples\Cube
```

`Magic.exe --help` lists the options. Durations are counted in frames. The exit codes are 0 for a clean
run, 1 for bad arguments or a crash, and 2 when `--fail-on-error` is set and an error was logged.

## Contributing and AI Usage
Look to the contributing documentation [here](CONTRIBUTING.md).

In regards to AI usage:

See `AGENTS.md` for the AI contributor standards used by AI agents in this project.
We recognize the potential of AI tools to assist in development and encourage their responsible use.
However, AI code is used with great care and scrutiny and is never blindly accepted. If you use AI to contribute, you should fully understand what the code is doing and be ready to explain, defend, and/or change it.
All AI-generated code must be reviewed and approved by a human before being merged and any AI-generated code must follow the same standards as human-written code.
