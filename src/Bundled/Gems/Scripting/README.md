# Scripting

Reads and runs the scripts prefabs attach to entities: provides `IScripts`
([Core's README](../../../Core/README.md#scripts)), and reads a `.cs` file as the class it declares, the `Script`
asset. What a script is, how it is written and how its code is compiled and reloaded is Core's; this gem reads a `.cs`
asset as the class named like the file, found in the code loaded (Core's `Assemblies`), and a name none of it
declares, or two classes have, fails the load. The default `.cs` asset is a behaviour that does nothing. How a prefab
names a script and its values is the [prefab loader's](../PrefabLoader/README.md).

## Settings

Each setting is documented on `ScriptingSettings` and shown in the settings window; both pace far behaviours
([Pacing](#pacing)), and a longest interval of one frame turns pacing off.

## Making one

Which scripts an entity has is its `Scripts` component, a few `.cs` asset handles: a prefab's part gives its entity
one from its scripts list, with each script's values beside, through the attach the world offers, and game code adds
or removes a handle as it changes any component. Every frame, before the behaviours run, this gem makes the copies
what the component says: a copy for each handle an entity has no copy of, with the values given for it or the
script's defaults, and the end of a copy whose handle is gone or whose entity is. A copy is made on the main thread,
while the entity names it:

1. **Made**: its one public constructor is called, given the entity it is attached to first when it asks for it, then a
   service for each other parameter, as a gem's constructor is, and an optional one its default when nothing provides
   it.
2. **Given its values**: each of the prefab's values is set on its property, so a property's initialiser is its default
   and a prefab's value replaces it.
3. **Started**: its `Start()` runs, with its values set and its entity's components there to read, and a system is added
   to the ECS, as the kind of system it is (each, when it is several), at once or, while the world loads, once it is
   shown.

A script starts in the frame after it is attached, so whatever spawned with its entity is all there. While a domain
loads in place of the world, unless its class is `[Impatient]`, it waits until the domain is there, then starts as
this gem readies the domain (it is one of the gems that ready a domain before it is shown,
[Core's README](../../../Core/README.md#the-world)), so what it spawns as it starts is readied with it. Held, its hooks
are not called and its systems are not added until this gem hears the loading is over, the frame after the world is
first shown, so that frame draws it as it loaded. An `[Impatient]` script, such as a loading domain's, starts and runs
while the loading domain shows; one that starts as the domain replacing the world is readied is held all the same, as
it belongs to the world not shown yet, so nothing of it, a label or a sound, shows over the loading domain's last
image. The readying is done once an update finds nothing left to start,
what the started scripts attached among it. A handle whose `.cs` asset does not load, or whose script cannot be made
or started, is an error once, and the entity goes without that script until it is attached again. A pipeline script
is made for no entity, with services alone, and asking for an `Entity` is the error.

When Core loads a new build of a project's code, this gem ends each live copy of a script read again and starts a new
one of its class as it is now, made for the same entity with the same values, so the entity and its components stay
while what the script kept in its own fields starts over; an entity a domain placed from a prefab is spawned again
anyway, as the prefab is read again too. A script in a gem takes a restart, as the gem does.

## Saved and put back

The world is saved as data across a restart ([Core's README](../../../Core/README.md#the-world)), and a script with
it, as JSON by name: its values, as a prefab names them, and the fields it kept for itself, by their own names (a
property's or a primary constructor parameter's, for the field the compiler made). Not what its constructor was
handed, a service, nor what it made rather than kept, a delegate, a task or a disposable such as a subscription, nor
a field JSON cannot hold, which is warned about once per class: a copy put back starts over without those, as its
constructor and initialisers leave them. An `Entity` is saved as its id and put back as the entity made for it, so a
script that keeps the entities it spawned keeps them.

Put back, in the frame the world is, a script is made as it is attached, its class found in the code loaded now as
reading its file finds it, its constructor given the entity and its services, then each saved value is set on its
property or field, and it runs on: `Start()` is not called, as it is as it was, so a script that spawns or subscribes
in `Start` does not do so twice, and one that measured something as it started keeps its measure. The world is
readied out of sight as it is put back, so the copy is held as one started while a domain is readied is, whatever its
class, and runs on from the frame after the world is first shown. A saved value its class no longer
has, or that no longer fits, is warned about once and left. Its file is loaded too, so a build replaces the copy as it
does any other.

## Running

- A behaviour's `Update`, `FixedUpdate` and `LateUpdate` are called in this gem's hooks, in the order the scripts were
  attached: after the ECS's systems for the phase, as this gem is made after the ECS's. A far one's `Update` and
  `LateUpdate` are called less often ([Pacing](#pacing)).
- A behaviour that throws is logged and ends; the others go on. A system is the ECS's to run, and to take off when it
  throws.
- A script ends when its entity is destroyed, by its domain closing, its prefab being spawned again after an edit, or
  anything else, at this gem's next hook: its systems come off the ECS, so one may run once more in the frame its
  entity goes, and it is disposed if it is `IDisposable`. When the run ends, every script ends in the gem's `Stop`,
  on the main thread, before the gems are disposed.

## Pacing

Every behaviour runs, but one far from the player is updated less often, as what is twice as far moves half as far on
screen. The player is where its cameras are: those whose entity carries the player tag, not one drawing into a mirror
or a minimap.

- Within the full-rate distance of the nearest of the player's cameras, `Update` and `LateUpdate` are called every
  frame; up to n times as far, every nth frame; and never less often than the longest interval the settings allow.
  Entities take turns by their id, so the far ones do not all fall on one frame.
- A call that was waited for is handed the time since the last one as its frame's delta, so what moves by delta
  moves as far; what reads the frame's clock is where it would be, and only steps more coarsely.
- `FixedUpdate` is called every step, however far: the fixed step is what a simulation's results depend on.
- An entity without a `Transform` is nowhere, so it is updated every frame; so is every entity while there is no
  player camera. A script on a player camera's own entity is at no distance from it.
- The cameras are where they are as of this gem's `Update`. A system is not paced: the ECS runs it over every entity
  its query has, each frame.

A behaviour that must see every frame, such as one reading a key the frame it is pressed, sees it on a near entity or
one without a `Transform`; a longest interval of one frame turns pacing off.
