# ArchEcs

The entity component system, built on [Arch](https://github.com/genaray/Arch). Provides `IEcs`, which Core's `World`
is built on ([The world](../../../Core/README.md#the-world)). Gems and scripts usually reach entities through the
world. It has no settings.

A component is an unmanaged struct that implements `IComponent`. A system names up to four components and runs over
every entity that has them. A system that names none runs over every entity. Adding a system returns a subscription;
dispose it to remove the system. Systems run in `Update`, `FixedUpdate` or `LateUpdate`, in the order they were added.
A parallel system runs on several threads at once, one chunk of entities each.

- Use it from the main thread only. A parallel system may read other entities from its own thread.
- Don't create or destroy entities, or add or remove components, inside a query loop or a parallel system. Collect the
  changes and apply them after. Setting a component the entity already has is fine.
- Reading a component an entity doesn't have, or using an entity that isn't alive, throws.
- A system that throws is logged as an error and removed.
