# Scripts

C# behaviours attached to entities by a chunk. A `.cs` file is a `Script` asset: its id is how a chunk names it, and
its class, the one `IScript` named like the file, is what runs. Nothing is compiled at run time: the class is already
in a loaded dll, and the CSharpScripting gem only finds it.

| Script | Id | Does |
| --- | --- | --- |
| `LoadingBar.cs` | 720 | Scales its entity along X by how far the loading world is (`width`, 1 by default). |
| `LoadingIcon.cs` | 721 | Puts the project's icon on its entity's material (`map`, `"colorMap"` by default). |

Both are for the loading domain, and both are `[Impatient]`: they run while the world they wait for is loading.

## In a chunk

```json
{ "name": "BarFill", "scripts": [ { "id": 720, "width": 0.6 } ], "components": { … } }
```

Every key beside `id` sets a public property of that name on the script (ignoring case), from its JSON value.

## Writing one

```csharp
internal sealed class Spin(Handle entity, IEcs ecs) : IBehavior
{
    public float DegreesPerSecond { get; set; } = 45f;

    public void Update(in Frame frame)
    {
        ref Transform transform = ref ecs.Get<Transform>(entity);
        transform.Rotation *= Quaternion.CreateFromAxisAngle(Vector3.UnitY, float.DegreesToRadians(DegreesPerSecond) * frame.Delta);
    }
}
```

- An `IBehavior` runs per entity; an `ISystem` is one instance that queries what it wants. The constructor asks for
  what it needs: the entity's `Handle`, and any service (`IEcs`, `World`, `IInput`, `Assets`, …).
- The class name is unique across every loaded dll (projects use `namespace <Project>.<Area>`), and the file is
  named after it.
- `[Impatient]` runs it while the world is still loading; `[AlwaysUpdate]` keeps it running every frame however far
  it is from the cameras (otherwise far scripts run less often).

## Who compiles it

| Where | Built by |
| --- | --- |
| `Resources/Scripts/` | `Magic.dll` itself |
| A project's `Assets/**/*.cs` | the project's csproj: `<Compile Include="Assets\**\*.cs" />` |

## Lifetime

A script asset is resolved when a chunk that names it spawns, and an instance is made per entity. Editing the `.cs`
does nothing on its own: rebuild the dll that compiles it and the host reloads it, and every script with it, without
a restart. A script that names a class no loaded dll has fails, with the reason in the log, and its entity spawns
without it.

## Adding a script

In a project, put `Name.cs` under `Assets/` (a sample keeps its own in `Domains/<Sample>/Scripts/`), build, and name
its id from a chunk's `scripts`. In `Resources/Scripts/`, only what the engine's own domains need; take the next id
of 720–799.
