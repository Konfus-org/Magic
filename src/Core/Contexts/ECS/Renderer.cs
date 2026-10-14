using Magic.Contexts.Assets;
using Magic.Interfaces;

namespace Magic.Contexts.ECS;

/// <summary>
/// Draws a model where its entity's <see cref="Transform"/> puts it, each of the model's meshes with the material in
/// the slot the mesh names: <c>{ "model": { "id": 514 }, "materials": { "slot0": { "id": 710 } } }</c>.
/// </summary>
public record struct Renderer : IComponent
{
    /// <summary>
    /// Seconds a fade takes (<see cref="Fade"/>).
    /// </summary>
    public const float FadeSeconds = 0.4f;

    public Handle<Model> Model;

    public MaterialSlots Materials;

    /// <summary>
    /// Whether it is fading in or out: dissolving into view, or out of it, over <see cref="FadeSeconds"/> from when it
    /// is set, so one thing can take another's place with neither popping. One fading in and one fading out from the same
    /// frame between them draw each pixel once. A gem that fades one out destroys it once the fade is over, and sets one
    /// that has faded in back to <see cref="RendererFade.None"/>.
    /// </summary>
    public RendererFade Fade;
}

/// <summary>
/// How a <see cref="Renderer"/> is fading (<see cref="Renderer.Fade"/>).
/// </summary>
public enum RendererFade : byte
{
    /// <summary>
    /// Drawn as it is.
    /// </summary>
    None,

    /// <summary>
    /// Dissolving into view.
    /// </summary>
    In,

    /// <summary>
    /// Dissolving out of view.
    /// </summary>
    Out,
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
