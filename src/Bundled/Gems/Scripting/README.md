# Scripting

Runs the scripts prefabs attach to entities: provides `IScripts` ([Core's README](../../../Core/README.md#scripts)), and
reads `.cs` files, as `Script` assets, for Core's `Assets`.

## Settings

In the project's `Presets/Default.preset`, by another preset, or with `--set Scripting.<key>=<value>`:

| Key | Default | |
| --- | --- | --- |
| `fullRateDistance` | `64` | Metres from the nearest of the player's cameras within which a behaviour is updated every frame ([Pacing](#pacing)). |
| `maxInterval` | `32` | The most frames between two updates of a far behaviour; 1 updates every behaviour every frame. |

## Scripts

A script is a `.cs` file with a `.meta` beside it, as any asset has, whose class is named like the file and is an
`IScript`: an `IBehavior` for what its entity does each frame, a system script of any kind the world adds
(`ISystemScript<T1>`, `IParallelSystemScript<T1, T2>`, …), or neither, when all it does is begin.

```csharp
internal sealed class Hover(Entity owner, World world) : IBehavior
{
    private double _restY;

    public float Height { get; set; }

    public void Start()
    {
        _restY = world.Get<Transform>(owner).Position.Y;
    }

    public void Update(in Frame frame)
    {
        world.Get<Transform>(owner).Position.Y = _restY + (Height * MathF.Abs(MathF.Sin((float)frame.Seconds)));
    }
}
```

Nothing is compiled at run time: a project's scripts are its code, which the csproj beside its `.magic` file compiles
from its `Assets/` and Core loads with the project ([Core's README](../../../Core/README.md#the-project-file)); a gem's
own, from its `Resources/`, are compiled with the rest of the gem; and Magic's, from the bundled resources
(`src/Bundled/Resources/`), by the project in that folder, into `Resources/Resources.dll` beside Magic, which Core loads
as it loads a project's code. Loading a script finds its class in the code loaded (`Code.Types`), in which a gem that
did not load, as Core reports, has no classes; a name none of it has, or two classes have, fails the load.

So editing a script takes a build, and no restart: Core watches the project's code and Magic's scripts where they are
built, loads a new build in place of the last, and has every script read again, as the class it is now
([Core's README](../../../Core/README.md#the-project-file)). This gem then ends each live copy of a script read again
and starts a new one of its class as it is now, made for the same entity with the same values, so the entity and its
components stay while what the script kept in its own fields starts over; an entity a domain placed from a prefab is
spawned again anyway, as the prefab is read again too. A script in a gem takes a restart, as the gem does.

## In a prefab

```json
{ "name": "Cube", "components": { … }, "scripts": [ { "id": 3312, "height": 1 } ] }
```

Each entry names a script by its id; every other key is a value for the script's public property of that name, as
Core's JSON dialect names it (camelCase). The gem that reads prefabs loads the script and reads each value as its
property's type, so a prefab's part holds its scripts, each with its values, as it holds its components
(`PrefabScript`). It makes none: a script is made for an entity.

## Making one

Scripts work as components do: a part holds them with their values, and each entity spawned from it gets its own copy,
made for it when it is attached (`Attach`), on the main thread, and started there and then, while the entity lives:

1. **Made**: its one public constructor is called, given the entity it is attached to first, then a service for each
   other parameter, as a gem's constructor is, and an optional one its default when nothing provides it. A constructor
   only keeps what it is given and does no work: the script's values are not set yet, and the whole prefab is not
   there for it to read.
2. **Given its values**: each of the prefab's values is set on its property, so a property's initialiser is its default
   and a prefab's value replaces it.
3. **Started**: its `Start()` runs, with its values set and its entity's components there to read, and a system is added
   to the ECS, through the `Add` that takes its kind (each, when it is several).

When a script starts is the world's to say: it attaches each once whatever spawned with its entity is all there, and
while a domain loads in place of the world, once the domain is there, unless its class is `[Impatient]`
([Core's README](../../../Core/README.md#the-world)). This gem makes and starts each as it is attached.

A script that has other than one public constructor, whose constructor with parameters does not take its `Entity`
first, that asks for what nothing provides, or that throws while it is made or started, is an error, and its entity
goes without it. A pipeline script is made for no entity (`Make`), with services alone, and asking for an `Entity` is
the error.

## Saved and put back

The world is saved as data across a restart ([Core's README](../../../Core/README.md#the-world)), and a script with
it, as JSON by name (`Save`): its values, as a prefab names them, and the fields it kept for itself, by their own
names (a property's or a primary constructor parameter's, for the field the compiler made). Not what its constructor
was handed, a service, nor what it made rather than kept, a delegate, a task or a disposable such as a subscription,
nor a field JSON cannot hold, which is warned about once per class: a copy put back starts over without those, as
its constructor and initialisers leave them. An `Entity` is saved as its id and put back as the entity made for it,
so a script that keeps the entities it spawned keeps them.

Put back (`Restore`), a script is made as it is attached, its constructor given the entity and its services, then
each saved value is set on its property or field, and it runs on: `Start()` is not called, as it is as it was, so a
script that spawns or subscribes in `Start` does not do so twice, and one that measured something as it started
keeps its measure. A saved value its class no longer has, or that no longer fits, is warned about once and left.

## Running

- A behaviour's `Update`, `FixedUpdate` and `LateUpdate` are called in this gem's hooks, in the order the scripts were
  attached: after the ECS's systems for the phase, as this gem is made after the ECS's. A far one's `Update` and
  `LateUpdate` are called less often ([Pacing](#pacing)).
- A behaviour that throws is logged and ends; the others go on. A system is the ECS's to run, and to take off when it
  throws.
- A script ends when its entity is destroyed, by its domain closing, its prefab being spawned again after an edit, or
  anything else, at this gem's next hook: its systems come off the ECS, so one may run once more in the frame its
  entity goes, and it is disposed if it is `IDisposable`. When the gem ends, every script ends, on the main thread.

## Pacing

Every behaviour runs, but one far from the player is updated less often, as what is twice as far moves half as far on
screen. The player is where its cameras are: those whose entity is tagged `player` (`Tag.Player`), not one drawing into
a mirror or a minimap.

- Within `fullRateDistance` of the nearest of the player's cameras, `Update` and `LateUpdate` are called every frame; up
  to n times as far, every nth frame; and never fewer than every `maxInterval`th. Entities take turns by their id, so
  the far ones do not all fall on one frame.
- A call that was waited for is handed the time since the last one as its frame's `DeltaSeconds`, so what moves by
  delta moves as far; what reads `Seconds` is where it would be, and only steps more coarsely.
- `FixedUpdate` is called every step, however far: the fixed step is what a simulation's results depend on.
- An entity without a `Transform` is nowhere, so it is updated every frame; so is every entity while there is no
  player camera. A script on a player camera's own entity is at no distance from it.
- The cameras are where they are as of this gem's `Update`. A system is not paced: the ECS runs it over every entity
  its query has, each frame.

A behaviour that must see every frame, such as one reading a key the frame it is pressed, sees it on a near entity or
one without a `Transform`; `maxInterval` 1 turns pacing off.

The default script, `Scripts/Default.cs`, is a behaviour that does nothing.
