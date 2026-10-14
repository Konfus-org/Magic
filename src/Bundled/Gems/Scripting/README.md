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
`IScript`: an `IBehavior` for what its entity does each frame, a system of any kind the world adds
(`ISystem<T1>`, `IParallelSystem<T1, T2>`, …) as well, or neither, when all it does is begin.

```csharp
internal sealed class Hover(World world) : IBehavior
{
    private Entity _entity;
    private double _restY;

    public float Height { get; set; }

    public void Start(Entity entity)
    {
        _entity = entity;
        _restY = world.Get<Transform>(entity).Position.Y;
    }

    public void Update(in Frame frame)
    {
        world.Get<Transform>(_entity).Position.Y = _restY + (Height * MathF.Abs(MathF.Sin((float)frame.Seconds)));
    }
}
```

Nothing is compiled at run time: a project's scripts are its code, which the csproj beside its `.magic` file compiles
from its `Assets/` and Core loads with the project ([Core's README](../../../Core/README.md#the-project-file)); a gem's
own, from its `Resources/`, are compiled with the rest of the gem; and Magic's, from the bundled resources
(`src/Bundled/Resources/`), with the rest of this one. Loading a script finds its class in the code loaded; a name none
of it has, or two classes have, fails the load. So editing a script takes a build and a restart, as code does not
reload.

## In a prefab

```json
{ "name": "Cube", "components": { … }, "scripts": [ { "id": 3312, "height": 1 } ] }
```

Each entry names a script by its id; every other key sets the script's public property of that name, as Core's JSON
dialect names it (camelCase). The gem that reads prefabs has this gem make the script (`IScripts.Make`) and sets the
values on it, read as their properties' types, so a prefab's part holds its scripts as it holds its components.

## Making one

Scripts work as components do: a part holds them made, with their values set, and each entity spawned from it gets its
own copy.

1. **Made** (`Make`): its one public constructor is called, each parameter given a service, as a gem's constructor is,
   and an optional one its default when nothing provides it. It never gets its entity, as a script in a prefab is made
   before there is one. Any thread, so a constructor only keeps what it is given.
2. **Attached** (`Attach`), on the main thread, and started there and then, while its entity lives: a copy is made as
   above, given each of the script's public property values, so a property's initialiser is its default and a prefab's
   value replaces it. Then the copy's `Start(entity)` runs, with the entity's components there to read, and a system is
   added to the ECS, through the `Add` that takes its kind (each, when it is several).

When a script starts is the world's to say: it attaches each once whatever spawned with its entity is all there, and
while a domain loads in place of the world, once the domain is there, unless its class is `[Impatient]`
([Core's README](../../../Core/README.md#the-world)). This gem starts each as it is attached.

A script that has other than one public constructor, asks for its entity or for what nothing provides, or throws while
it is made or started, is an error, and its entity goes without it.

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
