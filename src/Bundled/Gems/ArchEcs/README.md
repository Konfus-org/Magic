# ArchEcs

The entity world, through [Arch](https://github.com/genaray/Arch), which is managed code only, with no native
library: provides `IEcs`, which Core's `World` is built on ([Core's README](../../../Core/README.md#the-world)); gems
and game code reach the entities through the world.

A component is an unmanaged struct implementing `IComponent`, which `IEcs` asks of every type it takes. A system says
which components it looks at, up to four, or none to go over every entity. A gem that takes the world adds its systems
in its constructor and disposes the subscriptions in its `Dispose`. A parallel system runs on several threads at once,
a chunk each, so it writes only through its chunk; a system that is not parallel runs once a frame on the main thread,
with the whole query to loop over.

## Behaviour

- One Arch world, made with the gem and disposed with it, and the main thread's: a call from any other thread throws.
  A gem made after this one may use it in its constructor, before the main thread starts.
- Systems run in the gem's hook for their phase, `Update`, `FixedUpdate` or `LateUpdate`, in the order they were
  added, parallel or not, so before the hooks of the gems made after it (every gem that takes the world). A system for
  `Render`, the render thread's, is refused. A system added or taken off while a phase runs counts from the next time
  that phase runs.
- A system that throws is logged as an error and taken off; the phase goes on without it.
- A query's loop gathers the Arch chunks it matches as it starts and hands each over as spans over Arch's own arrays,
  cut to the chunk's entities. Each query's description and its walk over the chunks are made once and reused, and
  Arch caches the query, so a loop allocates nothing; a loop over the same query inside one gets a walk of its own.
- Entities are Arch's, bit for bit (`Entity` is laid out as Arch's), so a chunk's entities are handed over without
  being copied. The gem refuses to start if an Arch update lays its entity out differently.

## Off the main thread

- A parallel system's chunks are spread over Arch's own job threads, one chunk each, and the next system runs once
  all are done. Arch pools the jobs, so it allocates nothing. The system may read other entities on whichever thread
  it runs on; what it throws is thrown on the main thread once the rest are done, so it is taken off as any other.
- The job threads, one per core but the main and render threads' and at least one, are made as the main thread's
  first `Update` starts, before any system runs: Arch schedules only from the thread that made them. They end in the
  gem's `Stop`, on the main thread for the same reason; they would otherwise keep the process alive.
- A batch of entities is made from spans of components in one go, with Arch's bulk create, rather than moving each
  entity once per component added: what a worker built off the main thread arrives in one call. The entities come
  back in a span the gem reuses, good until the next batch.
- An entity whose component types are known only as the program runs, a prefab's part's, is made with all of them
  at once, boxed, through Arch's untyped create. Each type is found in Arch's registry, or through its typed
  registration the first time, so a struct is measured as the typed calls measure it. Such components are given to
  an entity that exists by adding the types it lacks in one move and setting them all, and every component an entity
  has is read back boxed the same way: a saved world is put back and saved through those, never a frame.

## Guards

Arch trusts its caller. The gem checks what Arch would let go wrong silently, and throws instead:

| Mistake | Arch alone |
| --- | --- |
| Any call from a thread other than the main one, but reads inside a parallel system | Races with the main thread's changes. |
| Changing the world's structure during a query's loop or a parallel system: making or destroying an entity, or adding or removing a component | Goes through, and leaves the chunk's spans stale. |
| Any call on an entity that is not alive (asking whether it is alive, or has a component, answers false instead) | Applies to whatever entity has the slot now. |
| Reading a component the entity lacks | Hands back a reference to nothing. |

Setting a component the entity already has, and writing through a chunk's spans, change no structure and are fine
inside a loop. Collect the rest and apply it after.
