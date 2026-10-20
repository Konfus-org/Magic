# Samples

The engine's sample project, laid out as a game is: `Samples.magic`, its `Assets/`, and the csproj beside them. It has
no gem of its own; its settings, `Assets/Presets/Default.preset`, pick the pipeline the renderer runs. Its data is
the files under `Assets/`, and its code is the scripts among them, which `Samples.csproj` compiles into
`Projects/Samples/` beside Magic, where Magic loads it with the project
([Core's README](../src/Core/README.md#the-project-file)). Build the solution, then run `Magic --project samples`.

The camera flies itself until it is flown: hold the right mouse button and move the mouse to look around, WASD moves
where it looks, E and Q go straight up and down, and Shift goes faster. On a gamepad the left stick moves, the right
stick looks, the right and left triggers go up and down, and the left shoulder goes faster. A left click, or the
gamepad's south button, throws a ball where the camera looks.

## The world

An open world to fly over and to stress the engine: the grid domain is 32 by 32 cells, 64 metres each, 2 km across.
Mountains with snow caps run along its north edge; a forest of pines and oaks over rolling hills fills the west; a city
of towers stands at its middle, around a clearing; farmland and villages lie to the east, lakes among hills to the
south-east, a desert of dunes and mesas to the south-west, and rolling plains between. None of it is made as the
engine runs: the cells are data, made once.

- **Prefabs.** Everything is a blockout of the engine's basic shapes, and a prefab under `Prefabs/`: a tower, a tree, a
  house, a hill (a flattened sphere sunk into the ground), a mountain (a cone under a snow cap), a lake (a flat disc of
  water in a ring of sand), a field, a dune, a mesa, and a ground tile tinting each region. A cell is mostly those
  prefabs placed, `{ "prefab": { "id": N }, "components": { "Transform": … } }`, so changing a tree's prefab changes
  every forest.
- **The player** is a prefab, `Prefabs/Player.prefab`, which `Samples.magic` names as its `"player"`: a camera tagged
  `player`, flown by `FlyCamera` at 30 metres a second. Until the controls first move it, it flies itself, so every
  run sees the world go past the same way, close up and far off: straight north at 40 metres a second, climbing gently
  past the glass sphere, over the field of lights and up an avenue of the city, then, 575 metres out, turns to look
  back at the world's middle and goes around it at 4 degrees a second. The domain puts it where its `globals.prefab`
  has an entity tagged `spawn`, at the clearing's edge, 3 metres up and looking north.
- **All of it is there.** The domain opens whole, every cell spawned in one frame behind the loading domain, the cubes
  in the clearing hovering and spinning, and whatever draws picks each thing's detail by its distance, so the whole
  world to its edge is drawn. The first run makes every model's clusters and cards, which takes a while; every later
  run reads them from `Cache/`.
- **One ground**, in `globals.prefab`, lies under the whole world, which each region's tiles cover.
- **It is solid.** The ground, the towers, the houses, the trunks of the trees, the rocks, the hills and the mountains
  carry a `Physics` component, so a thrown ball bounces off them, and a crate lies beside every house; the field's
  metal spheres are balls to knock about. In front of the spawn, `globals.prefab` drops a stack of crates, a
  bouncing ball and a crate onto a tilted slab of ice. The physics gem simulates only what is within its range of the
  player, so a world of twenty thousand props costs a few hundred bodies around the camera; its README says how the
  component drives a body.

## Scripts

A script is a `.cs` file with a `.meta` beside it, an `IScript`, attached to an entity by its id; it works as a
component does, each entity spawned getting its own copy with the prefab's values. A component a script uses is
declared beside it, under `Components/`, read again when the project is built, and a prefab names it as it names
Core's:

```json
"components": {
    "Transform": { "position": { "x": 16, "y": 0.5, "z": 16 } },
    "Spin": { "degreesPerSecond": 45 }
},
"scripts": [
    { "id": 3312, "height": 1 }
]
```

- A system is attached to an entity of its own, so it runs while that entity's domain is open: the grid's
  `globals.prefab` gives `SpinSystem`, which turns whatever has a `Spin`, an entity named Systems.
- A behaviour runs for the entity it is attached to, and the prefab sets its public properties: the clearing's
  cubes' `Hover` floats each up and down by its `height`, 0 unless its prefab says, from where `Start` found it.
- A script's constructor takes its entity first and then the services it uses, as a gem does, and keeps them for
  `Start` and the hooks to use: the player's `FlyCamera` takes `IInput`, starts facing the way its entity spawned, and
  flies the camera at the `speed` the prefab sets, cruising at its `cruise` out to its `radius`, then orbiting at its
  `orbit`, until flown.
- A script can make entities of its own in `Start` and end them when its entity goes, by being `IDisposable`: the
  grid's `Field` scatters as many spheres and coloured point lights as its prefab says over the ground, to see what
  thousands of each cost to draw.

The grid's look is data in its `globals.prefab` too: an entity with a `PostProcessing` names the effects the world is
drawn with, a pipeline, and the glass sphere is a transparent material, drawn after everything opaque and bending what
is behind it. The pipeline it names, `Pipelines/Daylight.pipeline`, is the engine's effects with the exposure metered
from the light its sun and sky shed rather than from what the view sees, so nearing a dark tower or the bright field
never changes how everything else looks, and with a height fog whose density by height is a curve,
`Curves/Haze.curve`: thick in the valleys, thinning over the towers and gone above the mountains.
