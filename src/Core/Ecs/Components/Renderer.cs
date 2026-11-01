using System.Runtime.CompilerServices;
using Magic.Assets;
using Magic.Assets.Types;

namespace Magic.Ecs.Components;

/// <summary>
/// Draws a model where its entity's <see cref="Transform"/> puts it, each of the model's meshes with the material in
/// the slot the mesh names. The same materials say how the entity's body grips and bounces and how it sounds.
/// </summary>
public record struct Renderer : IComponent
{
    public Handle<Model> Model;

    public MaterialSlots Materials;

    /// <summary>
    /// It casts no shadow and hides nothing from the light around it, as a lamp's shade around its own light: it is
    /// drawn, but light passes through it.
    /// </summary>
    public bool IsShadowless;
}

/// <summary>
/// A <see cref="Renderer"/>'s materials by slot, the one a model's mesh names (<see cref="Mesh.MaterialSlot"/>):
/// <see cref="Capacity"/> of them, inline so the component stays plain unmanaged data, indexed, an empty slot none.
/// </summary>
[InlineArray(Capacity)]
public struct MaterialSlots : IEquatable<MaterialSlots>
{
    public const int Capacity = 8;

    private Handle<Material> _first;

    public static bool operator ==(MaterialSlots left, MaterialSlots right) => left.Equals(right);

    public static bool operator !=(MaterialSlots left, MaterialSlots right) => !left.Equals(right);

    /// <summary>
    /// Whether both hold the same material in every slot.
    /// </summary>
    public readonly bool Equals(MaterialSlots other) => ((ReadOnlySpan<Handle<Material>>)this).SequenceEqual(other);

    public override readonly bool Equals(object? obj) => obj is MaterialSlots other && Equals(other);

    public override readonly int GetHashCode()
    {
        HashCode hash = default;
        foreach (Handle<Material> held in this)
        {
            hash.Add(held);
        }

        return hash.ToHashCode();
    }

    /// <summary>
    /// Puts <paramref name="material"/> in the first empty slot; false when it is none or every slot is taken.
    /// </summary>
    public bool Add(Handle<Material> material)
    {
        Span<Handle<Material>> held = this;
        int free = held.IndexOf(Handle<Material>.None);
        if (material == Handle<Material>.None || free < 0)
            return false;

        held[free] = material;

        return true;
    }
}
