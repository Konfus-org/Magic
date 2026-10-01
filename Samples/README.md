# Samples

Each folder is a project: a `<Name>.magic` file naming the domain to open, `Assets/Domains/<Name>/` holding
that domain, and `Assets/Scripts/` holding C# scripts that `<Name>.csproj` compiles into a dll. A sample builds
into its own `Build/` tree, laid out like the engine's (`Build/net10.0/<Config>/bin/` and `obj/`), and the host
finds the dll there. The samples sit in `Magic.slnx`, so `dotnet build Magic.slnx` builds the engine and all of them; a
single one builds with `dotnet build Samples\Cube`. Run one from the repository root:

```powershell
Build\net10.0\Debug\bin\Magic.exe --project Samples\Cube
Build\net10.0\Debug\bin\Magic.exe --project Samples\Grid
```

Caches land in `Cache\` next to `Magic.exe`. Screenshots (`--screenshots 1 --screenshot-delay 60`) go to `Screenshots\` next to `Magic.exe` (`Build\net10.0\<Configuration>\bin\Screenshots\<Project>_<frame>.png`). Log files are written only by a Release build, to `Logs\` beside it (`Build\net10.0\Release\bin\Logs\<Project>_<date>_<n>.log`).

In Visual Studio, set a sample as the startup project and press F5: each carries a launch profile
(`Properties/launchSettings.json`) that starts `Magic.exe` from the engine's build tree with the sample as its
project, from the repository root, with the same options as above.

## Gems

The `.magic` file says which engine gems load, by their assembly name (the same name `GemDependsOn` uses).
`"default"` stands for every gem in the engine's `bin\Gems\` folder; a file without a `gems` key gets
`["default"]`, and `[]` loads none of them.

```json
{ "name": "Cube", "entryPoint": { "id": 3001 }, "gems": ["ZLogging", "FlecsEcs", "SDL", "SDLWindowing", "SDLRender"] }
```

A project's own gems are never listed: any gem dll found under the project folder (each sample's
`Build/net10.0/<Config>/bin/<Name>.dll`) loads and hot reloads like an engine gem. `obj`, `Cache` and dot
folders are skipped, so a build's intermediate copy of the dll does not count twice.

## Scripts

A sample's scripts are assets: a `.cs` file under `Assets/Scripts/` with a `.meta` beside it, whose class a
chunk attaches to an entity by the file's id. The sample's dll has no gem in it, only those classes.

Most samples' `OrbitCamera.cs` is an `IBehavior` on the camera entity: its `Update` swings that camera around
the world's up axis through the origin, keeping its height, distance and tilt. `globals.chunk` lists it under
the camera's `"scripts"` with the speed picked for the scene (`{ "id": 3004, "degreesPerSecond": 30 }`).
RenderTexture's `Spin.cs` is a behaviour on the monkey the same way.

SplitScreen's `OrbitCamera.cs` is an `ISystem` instead: one instance, attached to an entity of its own, that
queries both cameras and turns them opposite ways.

Edit a script and rebuild the sample while it runs: the host sees the new dll and reloads the scripts without
a restart.

## A domain on disk

```
Domains/Cube/
    Cube.domain       { "chunkSize": 64 }          global data: seed, chunk size, time
    globals.chunk     always loaded (camera, sun)  any .chunk whose name is not a coordinate
    0_0_0.chunk       the cube at x=0, y=0, z=0    streamed by the cameras
```

A chunk `x_y_z.chunk` covers `[x * chunkSize, (x + 1) * chunkSize)` on each axis. Opening a domain spawns its
global chunks under `World.<Name>.Globals`; its cubes spawn under `World.<Name>.Chunks`. The streaming system keeps
the cube each camera stands in, the cubes within one chunk size of it, and every cube the camera's frustum touches
within `Render.ViewDist` (500 m by default; `Infinity` is every chunk in view), and unloads the rest two seconds after the last camera stopped wanting them.

A chunk is a list of entities: an optional name, tags, components keyed by their type name, and children.
A component is written as the struct's own JSON; any struct implementing `IComponent` in any loaded gem works.

```json
{ "entities": [ { "name": "Sun",
                  "components": { "Transform": { "rotation": { "x": 0.41, "y": 0.27, "z": -0.13, "w": 0.86 } },
                                  "DirectionalLight": { "color": { "x": 1, "y": 1, "z": 1 }, "intensity": 3 } } } ] }
```

## The samples

| Sample | Shows | Look for |
|---|---|---|
| Cube | The first material end to end: a checkerboard cube, reverse-Z depth, back-face culling | `Stats: … instances 2, visible 2+0` in a Debug build; a 2x2 checkerboard cube on dark blue |
| Monkey | Model import conventions | Suzanne faces the camera, her left ear on the viewer's right |
| RenderTexture | A camera drawing into a texture: `SecurityCamera`'s `"target": { "texture": { "id": 5010 } }` names `Textures/Monitor.rtex` (`{ "width": 512, "height": 512 }`), and `Monitor.mat` samples the same id on a standing plane. `Scripts/Spin.cs` adds a `Spin` component the chunk uses | the plane shows the spinning monkey side-on, live; edit the size in `Monitor.rtex` while it runs |
| SplitScreen | Two cameras in one window: `"viewport": "top_half"` and `"bottom_half"`, each culled and drawn on its own | two views orbiting opposite ways; Debug-build Stats count both |
| Grid | GPU-driven scale: 102 400 static entities in 100 chunks streamed by distance | `Streaming:` lines; `FPS:` above 60 in a Debug build (vsync off by default); `--set Render.ViewDist=Infinity` loads every chunk in view |
| Wall | Two-phase HiZ occlusion culling: a wall in front of a 16k grid | in a Debug build, the last `Stats:` visible count is a fraction of the instances |
| Fallbacks | The render failure looks, one cube each: a surface that does not compile (magenta), a material whose shader is not an asset (magenta checker), a model that is not an asset (red checker on the unit cube), a texture that is not an asset (red checker). All glow and breathe. Logs errors by design, so `--fail-on-error` returns 2 | fix `Assets/Shaders/BadCompile.surf.hlsl` or point `BadTexture.mat` at texture 36 while it runs and the cube heals |

Hot reload works on every file: edit `0_0_0.chunk` while Cube runs and the cube moves; edit `globals.chunk`
and the camera or sun change; edit `Cube.domain` and the domain reopens.
