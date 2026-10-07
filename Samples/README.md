# Samples

One project: `Samples.magic` opens the Hub (a floor, a question mark and a camera to fly), and
every domain under `Assets/` is a sample to portal into: one folder each, or a folder per variant under a
group (`City/Day`, `City/Night`), with `Shared/` for what several use. The scripts under `Assets/` compile into one dll
(`Samples.csproj`, built into `Samples/Build/`), where the host finds it. Build everything and run it from the
repository root:

```powershell
dotnet build Magic-Wizard.slnx
Build\net10.0\Debug\bin\Magic.exe --project Samples
```

In Visual Studio, the host's "Magic" launch profile does the same, and "Magic (WSL)" runs the Linux build in WSL
(`Tools\build-all.ps1 -Runtime linux-x64` first).

Caches land in `Cache\` next to `Magic.exe`. Screenshots (`--screenshots 1 --screenshot-delay 60`) go to `Screenshots\` next to `Magic.exe` (`Build\net10.0\<Configuration>\bin\Screenshots\<Project>_<frame>.png`). Log files are written only by a Release build, to `Logs\` beside it (`Build\net10.0\Release\bin\Logs\<Project>_<date>_<n>.log`).

## The console

The grave key opens it. The terminal the host was started from is a console too: a line typed there runs the same way, so a
`--headless` run is driven from it. `help` lists the commands; these come with the engine's `ConsoleCheats` gem:

| Command | Does |
|---|---|
| `portal` | Lists the domains there are: the sample list. |
| `portal CityAtNight` | Opens that domain in place of the world. A name, `CityAtNight.domain`, or a path; `additive` opens it on top of the world instead. |
| `portal Cave 0,2,-4` | The same, and once it is loaded puts the cameras that draw the main window at that position. `portal 0,5,-11` alone moves them now. |
| `summon Prop_Wagon.glb` | Puts the asset in the world 3 m in front of the camera: a model wearing the default material, a material (`summon Red.mat`) on the default model (a cube), a chunk (its entities under a root at the position), or a domain (opened on top). `summon Sphere.fbx 0,1,0` says where. A bare name is enough when only one asset has it; a name several share asks for the extension. |
| `screenshot`, `restore shot.png`, `exit` | A screenshot of the main window, the world as it was when one was taken, and quit. |
| `preset` | Says which preset is in use and lists them: the engine's `Presets/Toaster`, `Default`, `Fancy` and `MeltMyGPU`. |
| `preset Toaster` | Uses that one (a name, `Toaster.preset`, or a path); `--preset` picks it at start-up, and a project's own `Presets/Default.preset` replaces the engine's. |
| `set Gpu.Backend=vulkan ShadowPlan.distance=500` | `Owner.Property=value` as `--set` takes them, a settings class's or a pass's, as many as given, live until the next preset (what `--set` gave at start-up stays over every preset). The backend switches without a restart. |

What is summoned sits under `World.Summoned` and goes when a portal replaces the world.

The engine's `DebugTools` gem adds `get Lod`, `get ShadowPlan`: a settings class's values, or a pass's parameters,
the same ones the settings window (F4) shows. The window changes values as `set` does: live until the next preset;
nothing is saved.

## Adding a sample

A sample is a folder under `Assets/` (or under a group of variants, like `City/`): its domain and its chunks at the top, and whatever materials, shaders,
textures and scripts it alone uses in a subfolder per type (`Materials/`, `Shaders/`, `Textures/`, `Scripts/`), each
with its `.meta` id. What more than one sample uses lives in `Assets/Shared/<Type>/`. Scripts go in `namespace Samples.<Name>`, since one dll holds them
all and a chunk names a script by its class name, which must be unique. The `magicsample` template makes one
(`Tools/VSTemplates/README.md`):

```powershell
dotnet new magicsample -n Fountain -o Samples\Assets
dotnet build Samples
```

`portal` lists it from then on, and `portal Fountain` opens it. Add a row to the table below.

## Gems

The `.magic` file says which engine gems load, by their assembly name.
`"default"` stands for every gem in the engine's `bin\Gems\` folder; a file without a `gems` key gets
`["default"]`, and `[]` loads none of them. A name with a leading `-` leaves that gem out of `"default"`, and
whatever references it with it:

```json
{ "name": "Cave", "entryPoint": { "id": 3601 }, "gems": ["default", "-DebugTools"] }
```

A list without `"default"` loads only what it names and the gems those reference, so it must name every gem it
wants: the logger, the ECS, the platform, the renderers, `Scripting` and `CSharpScripting` for scripts, and so on.
Almost no gem references another: the one case is `EntityHierarchy`, which `DeferredRenderer` and `ChunkStreaming`
bring with them.

A project's own gems are never listed: any gem dll found under the project folder (here
`Build/net10.0/<Config>/bin/Samples.dll`) loads like an engine gem, and hot reloads. `obj`, `Cache` and dot
folders are skipped, so a build's intermediate copy of the dll does not count twice. A project gem named like an
engine gem is the one loaded: that is how a project replaces, say, `DeferredRenderer` with its own.

## Scripts

A sample's scripts are assets: a `.cs` file under its folder (`Assets/<Name>/Scripts/`) with a `.meta`
beside it, whose class a chunk attaches to an entity by the file's id. The dll has no gem in it, only those classes.

The scripts more than one sample uses are shared, in `Assets/Shared/Scripts/`: `CameraController.cs` is an `IBehavior`
on the camera and `OrbitSystem.cs` an `ISystem` (see Moving around); `Wander.cs` walks DaytimeCity's townsfolk
round their blocks (see The towns); `Turn.cs` is an `IBehavior` whose `Update` turns its entity around the world's
up axis (CityAtNight's searchlights, TV's monkey, each chunk listing it under the entity's `"scripts"` with the speed
picked for it), and `Ring.cs` takes CityAtNight's lamplighters round their yards.

Edit a script and rebuild the Samples project while it runs: the host sees the new dll and reloads the scripts
without a restart.

## Moving around

`Assets/Shared/Scripts/` holds the two scripts every sample's `globals.chunk` names by id, with the speeds picked
for the scene:

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
(SplitScreen, TV, GracefulFailures) stays still.

## A domain on disk

```
Cave/
    Cave.domain       { "chunkSize": 64 }          global data: the chunk size
    globals.chunk     always loaded (camera, sun)  any .chunk whose name is not a coordinate
    0_0_0.chunk       the cave at x=0, y=0, z=0    streamed by the cameras
```

A chunk `x_y_z.chunk` covers `[x * chunkSize, (x + 1) * chunkSize)` on each axis. Opening a domain spawns its
global chunks under `World.<Name>.Globals`; its cubes spawn under `World.<Name>.Chunks`. The streaming system keeps
the cube each camera stands in, the cubes within `Streaming.Radius` of it (128 m by default), and every cube the
camera's frustum touches within `Streaming.ViewDist` (`Infinity` by default: every chunk in view). It is greedy: after
those, the cubes no camera wants are loaded too, nearest first, until the spawned chunks fill the `Chunk` budget
(`Assets.Budgets`, 1024 MB by default), so the world is there whichever way a camera turns. Over the budget the cubes
out of view go, the farthest first, and a nearer cube takes the place of the farthest one kept.

A domain opened in place of the others (the entry point, or `World.Open` from a script) fills behind the loading
domain: it is not drawn and its scripts wait until what the cameras want and the fill are there and the renderer has
all of it, so it appears whole. `[Impatient]` on a script class makes it run as soon as its entity spawns
all the same. The loading domain is the engine's (the LoadingScreen gem's `Domains/Loading`: the project's icon over a bar) unless
the `.magic` file names another, which is a domain like any other:

```json
{ "name": "Cave", "entryPoint": { "id": 3601 }, "loading": { "id": 3090 } }
```

The engine's `LoadingBar` script (id 720) scales its entity along X by how far the world is, read from
`World.Domains`; on a model whose `.meta` puts its `"origin"` on its left edge (`Models/Bar.fbx`, id 520) that is a bar
filling from left to right. `--loading <path>` picks a loading domain for one run.

Far things get cheaper on their own (LODs). A model small on screen is drawn as a lesser version of itself, picked
per instance on the GPU and blended into, never switched to (a dither over a quarter of the threshold, `LOD_BLEND` in the cull passes);
a cube beyond 512 m is spawned as a stand-in of its chunk: a few entities drawing what never moves there (small
things as their impostor cards, boxes when they have none, bigger ones as their lowest mesh LOD while the stand-in's
budget allows) and up to four point lights standing for all its point and spot lights, with a glowing dot where
they were; no scripts. It is swapped for the real chunk as a camera comes near. Both are made the first time they
are needed, by whichever gem provides an `ILODGenerator<T>` (`ModelLodGenerator` for models, `ChunkLodGenerator`
for chunks), and kept in `Cache\Lods\` next to
`Magic.exe` until the source file changes. `--set Lod.Bias=2` keeps detail twice as long, `0.5` gives it up
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
                                  "DirectionalLight": { "color": { "r": 1, "g": 1, "b": 1 }, "intensity": 3 } } } ] }
```

## The towns

DaytimeCity and CityAtNight (`City/Day` and `City/Night`) are chunks on disk like any authored world: a 20 by 20
grid of `x_y_z.chunk` files beside each domain, one block of the village kit's terraced houses round a yard per chunk, market squares,
watchtowers and the town wall along the edge, which stream, unload and get stand-ins as any chunk does. They were
written once by a generator that is no longer in the samples, so nothing is built at start-up; edit a chunk as you
would any other. The day town's carts stand along the curbs, and its townsfolk are capsules that walk the street
round their block to a random spot, pause and walk on (`Scripts/Wander.cs`: the chunk gives the rectangle, the speed
and a `seed`, from which `Randomness` makes each walker's own sequence).

## Assets from elsewhere

`Assets/Shared/Models/Village` is part of Quaternius' Medieval Village MegaKit, `Assets/Shared/Models/Interior` his Ultimate
House Interior Pack and two of Kenney's furniture models (the TV and its cabinet), all CC0; `Assets/CREDITS.md`
says where each came from. The kit's glTF files were packed into single `.glb` files, its textures halved to 1024
and its roughness maps turned into the ORM maps `Pbr` reads (`Assets/Shared/Textures/Village`, `Assets/Shared/Materials/Village`).

## The samples

| Sample | Shows | Look for |
|---|---|---|
| DaytimeCity | Scale, streaming and culling in a walled medieval town of about 400 000 entities, 400 chunks on disk (see The towns): houses built from the village kit's walls, windows, doors, roofs and gables, yards, market squares, watchtowers, carts along the curbs and capsule townsfolk walking their blocks (`Scripts/Wander.cs`), under a sun and a `Sky`. Far chunks are stand-ins and far houses their lesser versions; near houses hide the streets behind them (two-phase HiZ occlusion) | `Streaming:` lines and `FPS:` in a Debug build; roofs and paving to the horizon with no gaps or pop-in; capsules strolling the streets; `--set Lod.Bias=0.25` makes the LOD switch obvious; a second run logs no `Generated`; `--set Assets.Budgets={"Chunk":8}` shows chunks out of view unloading |
| CityAtNight | Lighting at scale, and every lighting path, in the same town at night: lanterns on posts along every street (`PointLight`s with unlit bulbs), lit windows, shop doors with a warm `AreaLight` over them, a searchlight turning on each watchtower (`SpotLight`s, `Shared/Scripts/Turn.cs`), lamplighters' lanterns going round the yards and squares (`Shared/Scripts/Ring.cs`), about 4 000 lights in all, under a dim moon. The middle square has the glass fountain: a refracting sphere, a teal pane and an amber block (`Materials/Glass*.mat`) inside a ring of red, green, blue and amber panes (`Materials/Tint*.mat`). Two chunks south, the festival square hangs 192 lights over one spot, more than a cluster's 63, and one very bright light sits on a `hidden` entity | pools of warm light along the streets to the horizon, the far ones as glowing dots; the searchlights' cones sweeping; the tinted panes mixing their colours in any order and the glass bending what is behind it; the festival spot flashing pink in squares that read `TOO MANY LIGHTS` with each tile's count; no white glare from the hidden light |
| Cave | GI: a cave of rock with its mouth open to the sun and a blue `Sky`, a pool of lava in the back left corner (emissive `Materials/Lava.mat`), an old wagon and crates for contact shadows | the sun's patches through the mouth; the back of the cave lit by the sky and by the rock's bounce, going dark with the GI passes taken out of DeferredRenderer's `Pipelines/Default.pipeline`; the walls and floor round the lava glowing orange; dark creases under the wagon and between the crates (`Deferred.DebugView`, F4, Debug tab, shows each term on its own) |
| SplitScreen | Two cameras in one window (`"viewport": "top_half"` and `"bottom_half"`), each culled and drawn on its own, both on a living room: a couch facing a TV whose screen shows static (`Shaders/Static.surf.hlsl`, a sample surface shader: snow re-rolled 30 times a second, scanlines and a rolling band, its average light given to the GI) | both views show the couch and the TV's snow flickering; fly the top view, the bottom one stays; Debug-build Stats count both |
| TV | A camera drawing into a texture: in the same living room, the TV's screen (`Materials/Screen.mat`) samples `Textures/Screen.rtex` (640 by 360), which `StudioCamera`'s `"target": { "texture": { "id": 5010 } }` draws into. It films a show 200 m away (`3_0_0.chunk`): a monkey and a ring turning (`Shared/Scripts/Turn.cs`) on a stage under circling coloured lights | the TV plays the show live; edit the size in `Screen.rtex` while it runs |
| GracefulFailures | Every failure the engine shows instead of crashing, in a line, each with a caption under it saying what failed and why (`Scripts/Caption.cs`, world text, always in a Debug build, in Release while F3 is on): a surface that does not compile, a material whose shader is not an asset, a shader that is not a surface, a material that is not an asset, a texture that is not an asset, a render texture of width 0, a model that is not an asset, too many lights on one spot, a post whose shader is not an asset, an unknown component, a script that is not an asset and a script that throws | magenta and red checkers that glow and breathe with their `!!! ... !!!` reasons; `TOO MANY LIGHTS` over the crowded sphere; a magenta `BADPOST` band across the top; the last three only in the log. Logs errors by design, so `--fail-on-error` returns 2; fix `Shaders/BadCompile.surf.hlsl` while it runs and the cube heals |

Hot reload works on every file: edit `Cave/0_0_0.chunk` while Cave is open and the rocks move; edit its
`globals.chunk` and the camera or sun change; edit `Cave.domain` and the domain reopens.
