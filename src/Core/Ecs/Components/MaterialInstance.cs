using Magic.Files;
using Magic.Files.Types;

namespace Magic.Ecs.Components;

/// <summary>
/// A slot's material (<see cref="MaterialSlots"/>): the <see cref="Material"/> asset it draws with, and the params this
/// entity sets in place of the asset's, <c>renderer.Materials[0].Overrides["color"] = new Color(1, 0, 0)</c>. Plain
/// data inline in the component, saved with the world as the component differs from its prefab's. In a file, the handle
/// alone, <c>{ "id": 36 }</c>, or <c>{ "handle": { "id": 36 }, "overrides": { "color": { "r": 1, "a": 1 } } }</c>.
/// </summary>
public record struct MaterialInstance
{
    public Handle<Material> Handle;

    public MaterialOverrides Overrides;
}
