# Magic

A small C# game engine. `Magic` is a host that loads a project and its gems (plugin dlls), opens a window and
runs the frame loop. The ECS, windowing, rendering and logging all live in gems and hot reload while it runs.

## Building and running

Needs the .NET 10 SDK. Configurations: `Debug`, `Optimize` (optimised, full symbols), `Release`.

```powershell
dotnet build Magic.slnx
dotnet test Tests/Magic.UnitTests
dotnet test Tests/Magic.IntegrationTests
Build/net10.0/Debug/bin/Magic --project Samples/Cube
```

`Magic --help` lists the options. Exit codes: 0 clean, 1 bad arguments or a crash, 2 an error was logged under
`--fail-on-error`.

Windows and Linux (Vulkan) are supported; macOS is not yet. `-r <rid>` builds for another platform into its own
folder (`Build/net10.0/Debug-linux-x64/`); the "Magic (WSL)" launch profile runs that build in WSL.

## How it works

- **Host** (`Magic/Program.cs`): reads the project's `.magic` file, loads gems, and each frame hands one
  immutable `Frame` to every gem's `Update`, `FixedUpdate` (60 Hz), `LateUpdate` and `Render`.
- **Gems** (`Magic/Gems.cs`): a dll with one class implementing `IGem`. Its constructor parameters are its
  dependencies, the Core interfaces it implements are what it provides. A rebuilt dll is reloaded in place.
- **Scripts**: a `.cs` asset whose class is an `ISystem` or an `IBehavior`, compiled by the project's csproj
  and attached to entities in chunk files.
- **Assets** (`Magic/Services/Assets.cs`): addressed by id, not path; the id and type live in a `.meta` file
  beside each asset. Gems add formats with `IAssetLoader<T>`.
- **Events** (`Magic/Services/Events.cs`): one `Event` struct, published from any thread, delivered in
  `Frame.Events`.
- **World** (`Magic/Services/World.cs`): a stack of open domains. A domain is a folder of `x_y_z.chunk` files
  streamed in around the cameras.
- **ECS** (`Magic/Interfaces/IEcs*.cs`, `Gems/FlecsEcs`): components are structs implementing `IComponent`;
  systems are `ISystem`s added to the `Scheduler`.
- **Rendering** (`Gems/SDLRender`): GPU-driven on SDL_GPU, with passes and materials defined as data in
  `Resources/`.

## Folder structure

```
Magic/       The host: gem loader, services, contracts, core systems
Gems/        Engine gems
Resources/   Engine assets: shaders, materials, passes, models, textures
Samples/     Example projects (Samples/README.md)
Tests/       Unit and integration tests (Tests/README.md)
Tools/       dotnet new templates for projects and gems (Tools/VSTemplates/README.md)
Build/       All build output (not checked in)
```

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) and the [code standards](CODESTANDARDS.md).

AI-assisted contributions are welcome under the same standards as any other code: you must
understand, be able to explain, and be ready to change what you submit, and a human reviews and approves
everything before it merges.
