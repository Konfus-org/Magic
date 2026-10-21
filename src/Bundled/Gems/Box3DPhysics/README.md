# Box3DPhysics

Rigid-body physics through Box3D, bound as every native library is ([Gems](../README.md#native-libraries)). It
takes the world, to read and move entities, the assets, to shape bodies
like their models, and the threads, to hear when a model has loaded. It provides `IPhysics`, the questions a script
asks of the bodies; the bodies themselves are driven through Core's `Physics` and `Contacts` components
([Core's README](../../../Core/README.md#physics)), so this README says what this gem makes of them.

## Bodies

An entity with a `Transform` and a `Physics` is a body. Nothing else is written anywhere: the shape's size is the
model's.

- **The shape fits the model.** A model's bounds, found from its meshes' vertices once per model, scaled by the
  transform, are the box; the largest extent is the sphere's diameter; the height and the width of the footprint are
  the capsule's length and diameter; and the model's triangles, at the transform's scale, are the mesh. An entity with
  no model is a unit cube's size, so the engine's cube, sphere and capsule models at scale 1 are 1 m shapes. A bound
  with no size, a plane's thickness, is given a little, as a shape needs some. A mesh is for static bodies alone, as
  Box3D moves only convex shapes; a moving body asking for one is an error, and gets a box, as does a mesh whose model
  has no triangles.
- **The body waits for its model.** A model loads on a worker as every asset does, and the body is made in the first
  step after it has, until when the entity stays where it is. A model that does not load is an error, and the body is
  a unit cube. The models a domain's bodies are shaped like are fitted before the domain spawns (Core's `IPreloader`),
  so its bodies are there from its first step; only a body a script spawns later waits. A domain that replaces the
  world has its bodies made while it is readied out of sight, but takes no step then, nor in the frame it is first
  shown, so nothing has fallen or rolled by the time it is seen: its first step comes as the loading is told over, the
  frame after, and only steps them.
- **Mass** is what Box3D gives the shape by default unless the component says, when the body's mass and inertia are
  scaled to it, so a hollow crate is a light one of the same size.
- **Surface** is one of a few frictions, with Box3D's default as normal, and the stickiest a little rolling
  resistance, so a sticky ball stops rolling. Bodies that touch mix frictions by their geometric mean and bounce by
  the larger, so a sticky crate on ice slides a little, and anything on a trampoline bounces.

## The step

The world steps on a thread of the gem's own, as Box3D asks (its step blocks on the tasks it forks), while the frame
goes on. Once per fixed step, on the main thread, after the gems before it in the project's list have run theirs (so a
script's `FixedUpdate` in a project that lists this gem after the scripting one is seen the same step):

1. The step before is waited for, if it is still running, and its results taken in if they have not been
   (step 4): the world is the main thread's from here until it is handed over again.
2. Every entity with the two components is compared with the copy of them its body kept from the last step. A new
   entity gets a body. A change to anything that shapes the body, its motion, shape, surface, bounce, mass, model or
   scale, destroys the body and makes it again. A transform a script moved moves the body there: a dynamic or static
   one at once, a kinematic one driven there over the step, pushing whatever is in the way. A velocity a script set
   is the body's. A body whose entity, or component, is gone is destroyed.
3. The step is handed over: the physics thread steps the world, by the fixed step with the settings' substeps, and
   reads what moved and what every listening body touches.
4. In the gem's next hook once the step has ended, `Update` or the next fixed step, every body that moved writes its
   position and rotation to its transform, keeping the scale, and its velocities to its component, and keeps the copy
   the next step compares with. A transform or velocity a script wrote since the hand-over keeps the script's value,
   which the next step carries to the body. Then every entity with a `Contacts` has what its body touches written
   over it ([Contacts](#contacts)).

So data flows one way each step: the scripts' writes go in, the bodies' moves come out one hook later, and a script
reading a transform in `Update` sees where the body was after the last step that ended. Several fixed steps in one
frame each wait for the one before, so only the frame's last step overlaps the rest of the frame.

## Near the player

A world may hold far more bodies than a step should touch, so a body exists only while its shape reaches within the
settings' range of the nearest camera tagged `player`: its centre's distance less how far its shape reaches, so the
ground, or a mountain, is near from anywhere on it. An entity farther off has no body and waits where its components
say, with whatever velocity its last step left it, and gets its body back, moving as it was, when the player comes
near; one that has a body is dropped only somewhat past that range, so the edge does not make and drop it every
other step. With no player camera in the world everything is simulated, as there is no one to be near.

The step still walks every entity with the two components, to see which have come near, so what it does for a far
one is what bounds the step in a world of tens of thousands: a distance against the farthest any shape fitted so far
reaches at the entity's scale, from the transform alone, and nothing else. Only an entity that could be near is looked
up, its model, its shape and its body, and tested exactly. The bound is loose, so nothing near is missed; it only
costs the few entities between it and the exact edge the lookups.

## Queries

`IPhysics` is answered from the Box3D world between steps, on the main thread, a query asked while a step runs
waiting for it first: a cast is Box3D's closest ray cast, and an overlap its shape overlap with a sphere, over every
shape the filter admits. A shape's category bits are its body's motion, static, dynamic or kinematic, and a query's
mask is the motions asked for, so Box3D does the filtering; the masks shapes collide by stay at everything, so
bodies collide as they did. A shape found is mapped back to its entity through its body's user data, which holds the
body's slot, one up so that 0 is no body. A body out of the player's range has no shape, so it is neither hit nor
found, as it is not simulated. A cast in Update sees the bodies as the last step left them, which is where their
transforms say they are.

## Contacts

After each step, every entity with a `Contacts` has its body's touching contacts read, Box3D's manifolds with points,
and written over the component: the other shape's entity, the mean of the manifold points, the normal turned to
point away from the other body, and the sum of the total normal impulses as how hard the hit was. The phase comes
from what the component held before the step: a body it already held is Stay, a new one is Enter, and one it held
that is no longer touching is kept one more step as Exit, with its last point and normal. An entity that loses its
body, out of range, parts from them all. Reading contacts costs per listening body, which is why it is opt-in; of
the touching contacts read per body, the first few that have points are kept, as many as the component holds.

## Settings

Each is documented on `Box3DPhysicsSettings` and shown in the settings window. Gravity is read every step, so a world
can change it as it runs. The range trades the cost of a step against how far from the player the world moves: a
body past it waits where it is ([Near the player](#near-the-player)). The substep count trades time for steadiness:
each costs as much again of the solver, and more keep stacks and joints from sagging. The worker count is read only
as the gem starts, so changing it restarts the engine; with one, the solver runs on the physics thread alone, which
small worlds are best with, and only a world of thousands of bodies gains from more.

## What must hold

- Metres, kilograms and seconds, with +Y up and gravity straight down, as the engine's transforms are.
- The fixed step is Core's, run a few times in one frame to catch up after a slow one, so a step must take a small
  fraction of it, in a Debug build too. A step that takes longer than the time it simulates can never catch up: Core
  then runs its most every frame, the frame is that many steps long, and the world falls behind real time. So the
  cost of a step is bounded by the bodies near the player and a few arithmetic operations per entity farther off,
  never by a lookup per entity in the world.
- The world is one thread's at a time: the physics thread's from a step's hand-over until it has read the results,
  the main thread's otherwise. Every other Box3D call, making and moving bodies, the queries, destroying the world as
  the run ends, is the main thread's, after waiting for a step still running. The gem measures the step and how long
  the main thread waited for it, as metrics on its meter.
- Box3D's asserts end the process with a crash file, as the engine's do, and what it logs is a warning.
- Box3D is alpha software whose API changes between tags, and the generated names change with it: moving the pinned
  tag in the csproj means generating the bindings again and reading every name the gem uses against them.
