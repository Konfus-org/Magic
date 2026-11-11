using Magic.Assets;

namespace Magic.Ecs.Components;

/// <summary>
/// A slot's material (<see cref="MaterialSet"/>, <see cref="MaterialSlots"/>): the <see cref="Material"/> asset it
/// draws with, and the params this entity sets in place of the asset's. Plain data, saved with the world as the
/// component differs from its prefab's. A renderer's slot changes as a copy put back,
/// <c>slot = renderer.Materials[0]; slot.Overrides["color"] = new Color(1, 0, 0); renderer.Materials =
/// renderer.Materials.With(0, slot);</c>. In a file, the handle alone, <c>{ "id": 36 }</c>, or
/// <c>{ "handle": { "id": 36 }, "overrides": { "color": { "r": 1, "a": 1 } } }</c>.
/// </summary>
public record struct MaterialInstance
{
    public Handle<Material> Handle;

    public MaterialOverrides Overrides;
}
