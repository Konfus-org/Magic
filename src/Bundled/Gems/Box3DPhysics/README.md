# Box3DPhysics

Rigid-body physics with [Box3D](https://github.com/erincatto/box3d). Provides `IPhysics` for ray casts and overlap
queries.

An entity with a `Transform` and a `Physics` component is a body. Add a `Contacts` component to get what it touches
([Physics](../../../Core/README.md#physics)). Box3D is a native library
([Native libraries](../README.md#native-libraries)).

- **Shape.** A body's box, sphere, capsule or mesh is sized to the entity's model at the transform's scale. An entity
  with no model is a 1 m cube.
- **Steps.** The world steps once per fixed step, on its own thread. New positions and velocities are written back to
  the entities after each step. A script moves a body by writing its transform, and throws it by writing its velocity.
- **Range.** Only bodies near the nearest camera tagged `player` are simulated. Bodies farther away wait where they
  are. With no player camera, everything is simulated.

Its settings, such as gravity and the range, are on `Box3DPhysicsSettings` and show in the settings window (F4).

- Only a static body can have a mesh shape. A moving body that asks for one gets a box.
- A body waits for its model to load before it exists. A domain's bodies are ready from its first step.
- A body out of range isn't found by queries and has no contacts.
