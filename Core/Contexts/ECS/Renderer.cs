using Magic.Contexts.Assets;

namespace Magic.Contexts.Components;

/// <summary>Per-instance opt-outs from work the renderer does by default.</summary>
[Flags]
public enum RenderFlags : byte
{
    None = 0,

    /// <summary>Never cull for being small on screen: a distant landmark that must always be there.</summary>
    NoSizeCull = 1,
}

/// <summary>
/// Draws a <see cref="Model"/>; each of the model's parts picks one of the <see cref="Materials"/> slots.
/// The host's render system registers it with whatever <see cref="Interfaces.IRendering"/> is loaded and
/// records the result in a <see cref="RenderInstance"/>; setting it again re-registers.
/// </summary>
public struct Renderer : IComponent
{
    public Handle<Model> Model { get; set; }

    public MaterialSlots Materials { get; set; }

    public RenderFlags Flags { get; set; }
}

/// <summary>
/// Fixed number of material slots, inline in the component so it stays plain unmanaged data and plain JSON
/// (<c>{ "slot0": { "id": N } }</c>). Unused slots are <see cref="Handle{T}.None"/>. Index it like an array.
/// </summary>
public struct MaterialSlots
{
    public const int Capacity = 8;

    public Handle<Material> Slot0 { get; set; }

    public Handle<Material> Slot1 { get; set; }

    public Handle<Material> Slot2 { get; set; }

    public Handle<Material> Slot3 { get; set; }

    public Handle<Material> Slot4 { get; set; }

    public Handle<Material> Slot5 { get; set; }

    public Handle<Material> Slot6 { get; set; }

    public Handle<Material> Slot7 { get; set; }

    public Handle<Material> this[int index]
    {
        readonly get => index switch
        {
            0 => Slot0, 1 => Slot1, 2 => Slot2, 3 => Slot3, 4 => Slot4, 5 => Slot5, 6 => Slot6, 7 => Slot7,
            _ => throw new ArgumentOutOfRangeException(nameof(index)),
        };
        set
        {
            switch (index)
            {
                case 0: Slot0 = value; break;
                case 1: Slot1 = value; break;
                case 2: Slot2 = value; break;
                case 3: Slot3 = value; break;
                case 4: Slot4 = value; break;
                case 5: Slot5 = value; break;
                case 6: Slot6 = value; break;
                case 7: Slot7 = value; break;
                default: throw new ArgumentOutOfRangeException(nameof(index));
            }
        }
    }

    /// <summary>Slot 0 set, the rest empty: what most renderers need.</summary>
    public static MaterialSlots Of(Handle<Material> first) => new() { Slot0 = first };
}
