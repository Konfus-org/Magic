# Samples

The engine's sample project, laid out as a game is: `Samples.magic`, its `Assets/`, and the csproj beside them, which
compiles the scripts and components among the assets into `Projects/Samples/` beside Magic, where Magic loads them
with the project ([the project file](../src/Core/README.md#the-project-file)). It has no gem of its own, and no
settings of its own: the engine's defaults serve it. Its Run in an IDE, or `dotnet run --project samples`, builds what
it runs with and starts Magic on it ([building](../README.md#building), [command line](../README.md#command-line)).

The camera flies itself until it is flown: hold the right mouse button and move the mouse to look around, WASD moves
where it looks, E and Q go straight up and down, and Shift goes faster. On a gamepad the left stick moves, the right
stick looks, the right and left triggers go up and down, and the left shoulder goes faster. A left click throws a
ball at whatever the pointer is over, or where the camera looks when it is over nothing; the gamepad's south button
throws where the camera looks.

## The world

An open world to fly over and to stress the engine: one domain, `Assets/Domains/Grid/`, a grid of cells kilometres
across, each region of it a different look. None of it is made as the engine runs: the cells are data, made once, and
everything in them is a blockout of the engine's basic shapes, placed from the prefabs under `Assets/Prefabs/`
([Prefabs](../src/Bundled/Resources/Prefabs/README.md)), so changing a tree's prefab changes every forest. What
belongs to the whole world, the sun, the sky, the weather, the ground, the spawn, the post-processing and the
systems, is in the domain's `globals.prefab`, and the domain opens whole, as every domain does
([Domains](../src/Bundled/Resources/Domains/README.md), [the world](../src/Core/README.md#the-world)). The first run
makes every model's clusters and cards, which takes a while; every later run reads them from `Cache/`.

The player is `Assets/Prefabs/Player.prefab`, which `Samples.magic` names as its player: a camera tagged `player`
with the scripts that fly and arm it, whose speeds, and the flight the camera makes by itself until it is flown, are
the values the prefab sets. Much of what is placed carries a `Physics` component, so a thrown ball bounces off it, and
the physics gem simulates only what is within its range of the player, so the whole world costs the bodies around the
camera ([physics](../src/Core/README.md#physics)).

The world's look is data too: an entity in `globals.prefab` with a `PostProcessing` names the effects pipeline the
world is drawn with, `Assets/Pipelines/Daylight.pipeline`, the engine's effects with the exposure metered from the
light its sun and sky shed rather than from what the view sees, so nearing a dark tower or the bright field never
changes how everything else looks, and with a height fog whose density by height is a curve, `Assets/Curves/Haze.curve`
([Curves](../src/Bundled/Resources/Curves/README.md)). The sky is the engine's procedural one, drawn by the sun and
the weather beside it: the weather's wind carries the clouds and its coverage says how much of the sky they cover, so
changing the weather, or turning the sun, changes the sky and the light the world is lit by
([Materials](../src/Bundled/Resources/Materials/README.md#writing-a-sky)).

## Scripts and components

The project's code is the scripts under `Assets/Scripts/`, each a `.cs` asset a prefab attaches by id and gives its
values, and the components they use under `Assets/Components/`, which a prefab names as it names Core's
([Scripts](../src/Core/README.md#scripts), [Components](../src/Core/README.md#components)). The prefabs there are the
examples of both.

The samples have one of each kind of script. A system, which turns whatever carries the project's spin component, is
attached to an entity named Systems in `globals.prefab`, so it runs while the domain is open. Behaviours run for the
entity they are attached to, with the values the prefab sets: one floats the clearing's cubes up and down by a height
the prefab gives it, and two fly and arm the player's camera. One more makes entities of its own as it starts, as many
spheres and point lights as its prefab says, to see what thousands of each cost to draw, and ends them when its entity
goes by being disposable.
