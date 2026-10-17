using Magic.Contexts.Assets;
using Magic.Interfaces;

namespace Magic.Contexts.Components;

/// <summary>
/// Draws a model where its entity's <see cref="Transform"/> puts it, each of the model's meshes with the material in
/// the slot the mesh names: <c>{ "model": { "id": 514 }, "materials": { "slot0": { "id": 710 } } }</c>.
/// </summary>
public record struct Renderer : IComponent
{
    public Handle<Model> Model;

    public MaterialSlots Materials;
}

/// <summary>
/// A <see cref="Renderer"/>'s materials, a slot for each a model's meshes can name (<see cref="Mesh.MaterialSlot"/>).
/// Inline rather than an array, so the component stays plain unmanaged data; an empty slot is none.
/// </summary>
public record struct MaterialSlots
{
    public Handle<Material> Slot0;

    public Handle<Material> Slot1;

    public Handle<Material> Slot2;

    public Handle<Material> Slot3;

    public Handle<Material> Slot4;

    public Handle<Material> Slot5;

    public Handle<Material> Slot6;

    public Handle<Material> Slot7;
}
