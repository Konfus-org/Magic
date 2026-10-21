# Samples

The engine's sample project, laid out as a game is: `Samples.magic`, its `Assets/`, and the csproj beside them, which
compiles the scripts and components among the assets into `Projects/Samples/` beside Magic, where Magic loads them
with the project ([the project file](../src/Core/README.md#the-project-file)). It has no gem of its own, and no
settings of its own: the engine's defaults serve it. Its Run in an IDE, or `dotnet run --project samples`, builds what
it runs with and starts Magic on it ([building](../README.md#building), [command line](../README.md#command-line)).

The camera stays where a sample puts it until it is flown: hold the right mouse button and move the mouse to look
around, WASD moves where it looks, E and Q go straight up and down, and Shift goes four times as fast. On a gamepad the
left stick moves, the right stick looks, the right and left triggers go up and down, and the left shoulder goes faster.
A left click throws a ball at whatever the pointer is over, or where the camera looks when it is over nothing; the
gamepad's south button throws where the camera looks. Each sample says its own keys in a few lines across the top of
the screen, and labels what it shows with a few words over it.

## How the samples are laid out

Each sample is a domain, a folder under `Assets/Domains/` of a `.domain` file and the prefabs it spawns, and the
project opens the hub first ([Domains](../src/Bundled/Resources/Domains/README.md),
[the world](../src/Core/README.md#the-world)).
Every domain holds what belongs to its whole world in its `globals.prefab`: the sky, the sun, the weather, the
post-processing, the spawn, and an entity for the systems that run while it is open. A sample that is more than its
globals places prefabs of its own, as the terrain samples place the landscape. Nothing is made as the engine runs but
what a script makes on purpose: the domains' prefabs are data, written once.

The player, `Assets/Prefabs/Player.prefab`, which `Samples.magic` names as its player, is put at each domain's spawn as
the domain replaces the world: a camera tagged `player` with the scripts that fly and arm it, and the one that jumps
between the samples from the number keys, 0 for the hub and 1 to 6 for the samples in the order the hub's arches stand
in. While a domain replaces the world, the samples' own loading domain shows, named as the project's loading domain: its
script is impatient, as a loading domain's scripts must be to run before the world is there
([Scripts](../src/Core/README.md#scripts)).

What the samples share sits beside the domains, in a folder for each kind: prefabs, scripts, components, materials,
shaders, passes, pipelines, curves, models, textures and audio. A script is a `.cs` asset a prefab attaches by id and
gives its values, and a component is a struct a prefab names as it names Core's
([Scripts](../src/Core/README.md#scripts), [Components](../src/Core/README.md#components)).

## The samples

**The hub** shows getting from one domain to another. Each arch holds a script that, as the camera passes through it,
opens its sample in place of the world. A plate before them opens the Annex, a pavilion at the plaza's side, beside the
world: no loading screen, no player put anywhere, and its prefabs spawned where they say, as a domain opened beside
others is; flying over the plate again closes it, and what it spawned goes with it. Each sample has an arch back.

**Audio** is sound from the engine's primitives ([audio](../src/Core/README.md#audio)). A speaker circles where the
camera starts, heard from each side in turn; a row of them stands at three distances, each heard out to a range of its
own; one plays inside a closed room, muffled until the camera is at its doorway; the same clap sounds in a brick hall,
which echoes it, and in a carpeted room, which does not; a horn is heard only in front of it; a siren's pitch is swept
by a script; and a bell rings when a ball strikes it, its body's contacts setting its sound off. An ambience heard
everywhere plays or pauses on a key.

**Scripting** is the two kinds of script that run each frame. A parallel system bobs thousands of cubes in a wave,
over the ECS's threads; a system on the main thread ends the bodies a behaviour throws up from a fountain once their
time is up, as only the main thread may change which entities there are; and the engine's own way of turning
whatever has a spin. Behaviours run for one entity with the values their prefab gives them: cubes hovering each by its
own height, a head turning to face the camera and a light breathing.

**Physics** is bodies ([physics](../src/Core/README.md#physics)): a block on a ramp of each surface, slipping or
holding; balls of rising bounce; a stack and a run of dominoes, which a thrown ball knocks over; a platform a script
moves, a kinematic body carrying what rests on it; and a target that turns red for a moment when struck, by its
renderer being given another material, which is how one entity's look changes when its material is shared.

**Lights** is a courtyard at night under the moon: street lamps, coloured lights and spot lights on a wall, each a
light, and past the wall a field of thousands of balls and hundreds of lights, to see what many of each cost.

**Terrain** is the samples' own shaders. The terrain, 30 kilometres of mountains round a basin, has no uvs, so its
surface maps its textures in world space from the surface's position and its derivatives, from above on level ground
and from the sides on cliffs, and lays grass, forest floor, rock and snow by height, slope and the way the ground faces,
reading each texture again larger with distance so the far hillsides do not show it repeating. The forest's surface
answers the weather: under cloud its bark, needles and leaves darken and gloss as if wet, wind flecks the needles
lighter, and snow lies on whatever faces up above the snow line its material says
([Materials](../src/Bundled/Resources/Materials/README.md#writing-a-surface)). Its keys change all of it while it runs,
each the way the engine offers for it: the weather is a component, read every frame, so the sky, the fog and the trees
follow at once; the snow line is a param of the terrain's and the trees' materials, and the sky's haze and its clouds'
height params of its material, set on them while they are drawn; and a look is the params of the passes the world's
effects pipeline runs, the tonemap's exposure, the bloom and the samples' own grade, a pass after the tonemap, set by
the passes' names and eased from one look to the next ([assets](../src/Core/README.md#assets)).

**Valley** is day, night and light from everywhere. The player starts inside a log cabin on the basin's floor; the sun
and the moon, directional lights at their real brightness, are turned about a tilted axis by the spin system, so a day
passes in about twelve minutes, sped up while a key is held: the sun comes in through the glass and lights the room
off its floor, sets red, and the lantern and the fire, lost in the day, light the room at night. The exposure is
metered from what the view sees, so the eyes open to the room coming in out of the sun. The snow line and the weather
change on the terrain sample's keys here too.

## Assets

Every asset here is CC0: the textures and the cabin's furnishings are Poly Haven's, the bell is from Kenney's impact
sounds, and the rest, the trees, the cabin, the other sounds, the forest's placements and the domains' prefabs, were
made for the samples by scripts that are not kept, as their output is the asset. A model's materials are in the slots
its file lists them in ([Models](../src/Bundled/Resources/Models/README.md)); a model exported from Blender keeps its
slots when its faces are in slot order ([the model loader](../src/Bundled/Gems/AssimpModelLoader/README.md)). The
first run makes every model's clusters and cards, the terrain's for minutes; every later run reads them from `Cache/`.
