using Magic.Contexts.Assets;
using System.Runtime.CompilerServices;

namespace Magic.Contexts.Components;

/// <summary>Per-instance opt-outs from work the renderer does by default.</summary>
[Flags]
public enum RenderFlags : byte
{
    None = 0,

    /// <summary>Never cull for being small on screen: a distant landmark that must always be there.</summary>
    NoSizeCull = 1,

    NoShadow = 2,

    /// <summary>Leave out of global illumination.</summary>
    NoGi = 4
}

/// <summary>
/// Draws a <see cref="Model"/>; each of the model's parts picks one of the <see cref="Materials"/> slots.
/// Named MeshRenderer rather than Renderer so it never clashes with the render gem's own class.
/// </summary>
public struct Renderer
{
    public Handle<Model> Model { get; set; }
    public MaterialSlots Materials { get; set; }
    public RenderFlags Flags { get; set; }
}

/// <summary>
/// Fixed number of material slots, inline in the component so it stays plain unmanaged data. Unused slots are
/// <see cref="Handle{T}.None"/>. Index it like an array or iterate it as a span.
/// </summary>
[InlineArray(Capacity)]
public struct MaterialSlots
{
    public const int Capacity = 8;
    private Handle<Material> _slot0;
}
