using Magic.Assets;

namespace Magic.Ecs.Components;

/// <summary>
/// The material of a slot: the <see cref="Material"/> asset it draws with, and the params this entity sets over those
/// of the asset.
/// </summary>
/// <remarks>
/// A <see cref="MaterialSet"/> or <see cref="MaterialSlots"/> holds one for each slot. It is plain data, saved with the
/// world when the component differs from that of its prefab. To change the slot of a renderer, put back a changed
/// copy: <c>slot = renderer.Materials[0]; slot.Overrides["color"] = new Color(1, 0, 0); renderer.Materials =
/// renderer.Materials.With(0, slot);</c>. In a file, it is the handle alone, <c>{ "id": 36 }</c>, or
/// <c>{ "handle": { "id": 36 }, "overrides": { "color": { "r": 1, "a": 1 } } }</c>.
/// </remarks>
public record struct MaterialInstance
{
    public Handle<Material> Handle;

    public MaterialOverrides Overrides;
}
