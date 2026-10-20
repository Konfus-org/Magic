# Box3DPhysics

Rigid-body physics through Box3D, built here from its source as every native library is
([Gems](../README.md#native-libraries)). It takes the world, to read and move entities, the assets, to shape bodies
like their models, and the threads, to hear when a model has loaded. It provides `IPhysics`, the questions a script
asks of the bodies; a body itself is driven entirely through the `Physics` component in Core, and what it touches
reaches the game through `Contacts`, so any other physics gem could stand in its place without a prefab or script
changing.

## One component

An entity with a `Transform` and a `Physics` is a body. The component says what moves it (`motion`: static, dynamic or
kinematic), what it collides as (`shape`: box, sphere, capsule or mesh), how it grips (`surface`, from slippery to
sticky), how much it bounces, and its mass; its velocities are the body's, read after each step and written to throw
it. Nothing else is written anywhere: the shape's size is the model's.

```json
"Physics": { "motion": "dynamic", "surface": "slippery", "bounce": 0.3 }
```

- **The shape fits the model.** A model's bounds, found from its meshes' vertices once per model, scaled by the
  transform, are the box; the largest extent is the sphere's diameter; the height and the width of the footprint are
  the capsule's length and diameter; and the model's triangles, at the transform's scale, are the mesh. An entity with
  no model is a unit cube's size, so the engine's cube, sphere and capsule models at scale 1 are 1 m shapes. A bound
  with no size, a plane's thickness, is given a centimetre, as a shape needs some. A mesh is for static bodies alone,
  as Box3D moves only convex shapes; a moving body asking for one is an error, and gets a box.
- **The body waits for its model.** A model loads on a worker as every asset does, and the body is made in the first
  step after it has, until when the entity stays where it is. A model that does not load is an error, and the body is
  a unit cube.
- **Mass** is the volume's at the density of water unless the component says, when the body's mass and inertia are
  scaled to it, so a hollow crate is a light one of the same size.
- **Surface** is one of five frictions, with Box3D's default, 0.6, as normal, and the stickiest a little rolling
  resistance (`Surfaces`). Bodies that touch mix frictions by their geometric mean and bounce by the larger, so a
  sticky crate on ice slides a little, and anything on a trampoline bounces.

## The step

Once per fixed step, after the gems before it in the project's list have run theirs (so a script's `FixedUpdate`
in a project that lists this gem after the scripting one is seen the same step):

1. Every entity with the two components is compared with the copy of them its body kept from the last step. A new
   entity gets a body. A change to anything that shapes the body, its motion, shape, surface, bounce, mass, model or
   scale, destroys the body and makes it again. A transform a script moved moves the body there: a dynamic or static
   one at once, a kinematic one driven there over the step, pushing whatever is in the way. A velocity a script set
   is the body's. A body whose entity, or component, is gone is destroyed.
2. The world steps, by the fixed step with the settings' substeps.
3. Every body that moved writes its position and rotation to its transform, keeping the scale, and its velocities to
   its component, and keeps the copy the next step compares with.
4. Every entity with a `Contacts` has what its body touches written over it ([Contacts](#contacts)).

So data flows one way each step: the scripts' writes go in, the bodies' moves come out, and a script reading a
transform in `Update` sees where the body was after the last step. Nothing of the body is cached in a script's hands:
there is no handle to hold, as the component is the whole interface.

## Near the player

A world may hold far more bodies than a step should touch, so a body exists only while its shape reaches within the
settings' `range` of the nearest camera tagged `player`: its centre's distance less how far its shape reaches, so the
ground, or a mountain, is near from anywhere on it. An entity farther off has no body and waits where its components
say, with whatever velocity its last step left it, and gets its body back, moving as it was, when the player comes
near; one that has a body is dropped only past a quarter again as far, so the edge does not make and drop it every
other step. With no player camera in the world everything is simulated, as there is no one to be near. Making a
body costs microseconds, so a few hundred entering in one step, a village coming into range, is nothing to the step.

## Queries

`IPhysics` is answered from the Box3D world between steps, on the main thread: `Cast` is Box3D's closest ray cast,
and `Overlap` its shape overlap with a sphere, over every shape the filter admits. A shape's category bits are its
body's motion, static, dynamic or kinematic, and a query's mask is the `Motions` asked for, so Box3D does the
filtering; the masks shapes collide by stay at everything, so bodies collide as they did. A shape found is mapped
back to its entity through its body's user data, which holds the body's slot, one up so that 0 is no body. A body out
of the player's range has no shape, so it is neither hit nor found, as it is not simulated. A cast in Update sees
the bodies as the last step left them, which is where their transforms say they are.

## Contacts

After each step, every entity with a `Contacts` has its body's touching contacts read, Box3D's manifolds with points,
and written over the component: the other shape's entity, the mean of the manifold points, the normal turned to
point away from the other body, and the sum of the total normal impulses as how hard the hit was. The phase comes
from what the component held before the step: a body it already held is Stay, a new one is Enter, and one it held
that is no longer touching is kept one more step as Exit, with its last point and normal. An entity that loses its
body, out of range, parts from them all. Reading contacts costs per listening body, which is why it is opt-in; up to
64 touching contacts are read per body, and the first eight that have points are kept.

## What must hold

- Metres, kilograms and seconds, with +Y up and gravity straight down, as the engine's transforms are.
- The fixed step is Core's (1/60 s); Box3D substeps it, 4 times unless the settings say, and runs its solver on its
  own threads when `workers` is above 1, which only a world of thousands of bodies needs.
- Box3D's asserts end the process with a crash file, as the engine's do, and what it logs is a warning.
- A Box3D build is pinned to its tag in the csproj; it is alpha software whose API changes between tags, so moving the
  tag means reading the generated names again.
