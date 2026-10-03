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

RenderTexture's `Spin.cs` is an `IBehavior` on the monkey: its `Update` turns that entity around the world's up
axis. `0_0_0.chunk` lists it under the monkey's `"scripts"` with the speed picked for the scene.

The two scripts every sample uses are shared, in `SampleShared/Assets/Scripts/` (see Moving around):
`CameraController.cs` is an `IBehavior` on the camera, and `OrbitSystem.cs` is an `ISystem`: one instance,
attached to an entity of its own, that queries the cameras.

Edit a script and rebuild the sample while it runs: the host sees the new dll and reloads the scripts without
a restart.

## Moving around

`SampleShared/` is a gem every sample's csproj references, so its dll is built into each sample's `Build/` tree
and loads with it. The gem adds `SampleShared/Assets/` to the asset folders, so every sample's `globals.chunk`
names the same two scripts by id, with the speeds picked for the scene:

```json
{ "name": "Orbit", "scripts": [ { "id": 3051, "degreesPerSecond": 4 } ] },
{ "name": "Camera", "scripts": [ { "id": 3050, "speed": 40 } ], "components": { "Camera": {} } }
```

`CameraController` (3050) flies its camera: hold the right mouse button to look around with the mouse and move
with W A S D, Q (down) and E (up); shift moves faster. `speed` is metres per second; `boost` and `sensitivity`
are optional. Nothing moves while the button is up, so the console and debug windows keep the keyboard.

`OrbitSystem` (3051) swings every camera around the origin, so a run with nobody at the controls (`--lifetime`,
a screenshot, an agent testing streaming) still sees the scene from all sides. The first press of the right
mouse button stops it for good; editing the chunk brings it back. A sample without the `Orbit` entity
(RenderTexture) stays still.

## A domain on disk

```
Domains/Cube/
    Cube.domain       { "chunkSize": 64 }          global data: seed, chunk size, time
    globals.chunk     always loaded (camera, sun)  any .chunk whose name is not a coordinate
    0_0_0.chunk       the cube at x=0, y=0, z=0    streamed by the cameras
```

A chunk `x_y_z.chunk` covers `[x * chunkSize, (x + 1) * chunkSize)` on each axis. Opening a domain spawns its
global chunks under `World.<Name>.Globals`; its cubes spawn under `World.<Name>.Chunks`. The streaming system keeps
the cube each camera stands in, the cubes within `Streaming.Radius` of it (128 m by default), and every cube the
camera's frustum touches within `Render.ViewDist` (`Infinity` by default: every chunk in view). It is greedy: after
those, the cubes no camera wants are loaded too, nearest first, until the spawned chunks fill the `Chunk` budget
(`Assets.Budgets`, 1024 MB by default), so the world is there whichever way a camera turns. Over the budget the cubes
out of view go, the farthest first, and a nearer cube takes the place of the farthest one kept.

A domain opened in place of the others (the entry point, or `World.Open` from a script) fills behind the loading
domain: it is not drawn and its scripts wait until what the cameras want and the fill are there and the renderer has
all of it, so it appears whole. `[RunOnLoading]` on a script class makes it run as soon as its entity spawns
all the same. The loading domain is the engine's (`Resources/Domains/Loading`: the project's icon over a bar) unless
the `.magic` file names another, which is a domain like any other:

```json
{ "name": "Cube", "entryPoint": { "id": 3001 }, "loading": { "id": 3090 } }
```

The engine's `LoadingBar` script (id 720) scales its entity along X by how far the world is, read from
`World.Active`; on a model whose `.meta` puts its `"origin"` on its left edge (`Models/Bar.fbx`, id 520) that is a bar
filling from left to right. `--loading <path>` picks a loading domain for one run.

Far things get cheaper on their own (LODs). A model small on screen is drawn as a lesser version of itself, picked
per instance on the GPU and blended into, never switched to (a dither over `Culling.LodBlend` of the threshold);
a cube beyond 512 m is spawned as a stand-in of its chunk, a few entities drawing what
never moves there (small things as boxes, the smallest not at all) and up to four point lights standing for all its point and spot lights with a glowing dot where each was, with no scripts, and swapped for the real
chunk as a camera comes near. Both are made the first time they are needed, by whichever gem provides an
`ILODGenerator<T>` (`MeshLods` for models, the engine itself for chunks), and kept in `Cache\Lods\` next to
`Magic.exe` until the source file changes. `--set Render.LodBias=2` keeps detail twice as long, `0.5` gives it up
twice as soon. To author them instead, name them in the asset's `.meta`: `"lods": { "0.25": 1234 }` is the id of
the model to draw under a quarter of the view's height (for a chunk, the id of the chunk to spawn beyond that many
metres). A script far from every camera runs less often too: every frame within 64 m, then every 2nd, 4th, up to
every 32nd frame as the distance doubles, with the time it waited as its delta; `[AlwaysUpdate]` on the class opts out.

A chunk is a list of entities: an optional name, tags, components keyed by their type name, and children.
A component is written as the struct's own JSON; any struct implementing `IComponent` in any loaded gem works.
Two tags mean something to the engine: `static` (never moves: its place is computed once and it is uploaded once)
and `hidden` (not drawn, nor anything under it; its lights and glows do not show either). Both are plain tags in
the file and in `Tags`; the engine keeps a marker component under each so its queries can filter on them.

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
| Lods | Levels of detail: three rows of monkeys from the camera out to 1.2 km | near monkeys are the full model, small ones the smooth lesser version, chunks beyond 512 m one stand-in entity each (`loaded as LOD 1` with `--verbose`); `--set Render.LodBias=0.25` makes the switch obvious, `=100` shows full detail everywhere; a second run logs no `Generated` |
| Lights | Deferred lighting: six `PointLight`s circling a ring of shapes (`Scripts/Circle.cs`), each drawn as a small unlit ball of its colour, and two `SpotLight`s overhead turning (`Scripts/Turn.cs`), under a dim blue moon | pools of colour crossing the floor and the shapes, mixing where they overlap; the spots' cones sweep round; every light reaches only its `range` |
| LightStress | Lights at scale, every path of the lighting: 400 chunks (1.28 km a side) with 16 lights each, about 6 400 in all. Per chunk twelve still `PointLight`s, two `SpotLight`s and two point lights a script moves (`Scripts/Circle.cs`); chunks beyond 512 m are stand-ins, whose lights are the chunk's merged into at most four, each of the chunk's own lights still showing as a glowing dot; in front of the camera 192 lights share one spot, more than a cluster's 63, and one very bright light sits on a `Hidden` entity | `--verbose` logs `… lights` (thousands, far more than the near chunks hold) and the frame time; pools of colour and dots of light reach the horizon, with no pink there; the crowded spot flashes pink in squares that read `TOO MANY LIGHTS` with each tile's count under it: the tiles too many lights reach, shown as the failure it is; no white glare from the hidden light |
| RenderTexture | A camera drawing into a texture: `SecurityCamera`'s `"target": { "texture": { "id": 5010 } }` names `Textures/Monitor.rtex` (`{ "width": 512, "height": 512 }`), and `Monitor.mat` samples the same id on a standing plane. `Scripts/Spin.cs` adds a `Spin` component the chunk uses | the plane shows the spinning monkey side-on, live; edit the size in `Monitor.rtex` while it runs |
| SplitScreen | Two cameras in one window: `"viewport": "top_half"` and `"bottom_half"`, each culled and drawn on its own | two views orbiting until you fly the top one; Debug-build Stats count both |
| Grid | GPU-driven scale: 410 000 static entities in 400 chunks (1.28 km a side), 1024 small shapes and one 16 m tower in each, streamed by distance. Far chunks are stand-ins, and anything under a pixel is not drawn, so towards the horizon the small shapes go and the towers stay | `Streaming:` lines; `FPS:` above 60 in a Debug build (vsync off by default); every chunk in view loads, whatever the distance; `--set Render.ViewDist=200` limits it and `--set Assets.Budgets.Chunk=8` shows chunks out of view unloading |
| Wall | Two-phase HiZ occlusion culling: a wall in front of a 16k grid | in a Debug build, the last `Stats:` visible count is a fraction of the instances |
| Fallbacks | The render failure looks, one cube each: a surface that does not compile (magenta), a material whose shader is not an asset (magenta checker), a model that is not an asset (red checker on the unit cube), a texture that is not an asset (red checker). All glow and breathe, and each carries its reason as red `!!! ... !!!` text (always in a Debug build; in a Release build while F3 is on). Logs errors by design, so `--fail-on-error` returns 2 | fix `Assets/Shaders/BadCompile.surf.hlsl` or point `BadTexture.mat` at texture 36 while it runs and the cube heals |

Hot reload works on every file: edit `0_0_0.chunk` while Cube runs and the cube moves; edit `globals.chunk`
and the camera or sun change; edit `Cube.domain` and the domain reopens.
