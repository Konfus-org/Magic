using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Magic.Contexts.Assets;
using Magic.Interfaces;
using Magic.Internal;

namespace Magic.Contexts.Components;

/// <summary>
/// Draws a model where its entity's <see cref="Transform"/> puts it, each of the model's meshes with the material in
/// the slot the mesh names: <c>{ "model": { "id": 514 }, "materials": [ { "id": 710 } ] }</c>.
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
/// Built in order, <c>{ grass, rock }</c> filling slots 0 and 1; a slot left empty before a filled one is set by index.
/// As JSON, an array of handles by slot, <c>[ { "id": 710 } ]</c>.
/// </summary>
[InlineArray(Capacity)]
[JsonConverter(typeof(MaterialSlotsJsonConverter))]
[SuppressMessage(
    "Naming",
    "CA1710:Identifiers should have correct suffix",
    Justification = "Enumerable only so { material } builds it; it is a renderer's slots, not a collection.")]
public struct MaterialSlots : IEquatable<MaterialSlots>, IEnumerable<Handle<Material>>
{
    public const int Capacity = 8;

    private Handle<Material> _first;

    public static bool operator ==(MaterialSlots left, MaterialSlots right) => left.Equals(right);

    public static bool operator !=(MaterialSlots left, MaterialSlots right) => !left.Equals(right);

    /// <summary>
    /// Whether both hold the same material in every slot.
    /// </summary>
    public readonly bool Equals(MaterialSlots other)
    {
        for (int i = 0; i < Capacity; i++)
        {
            if (this[i] != other[i]) return false;
        }

        return true;
    }

    public override readonly bool Equals(object? obj) => obj is MaterialSlots other && Equals(other);

    public override readonly int GetHashCode()
    {
        HashCode hash = default;
        for (int i = 0; i < Capacity; i++)
        {
            hash.Add(this[i]);
        }

        return hash.ToHashCode();
    }

    /// <summary>
    /// Puts <paramref name="material"/> in the first empty slot; false when it is none or every slot is taken.
    /// </summary>
    public bool Add(Handle<Material> material)
    {
        if (material == Handle<Material>.None) return false;

        for (int i = 0; i < Capacity; i++)
        {
            if (this[i] == Handle<Material>.None)
            {
                this[i] = material;

                return true;
            }
        }

        return false;
    }

    readonly IEnumerator<Handle<Material>> IEnumerable<Handle<Material>>.GetEnumerator()
    {
        for (int i = 0; i < Capacity; i++)
        {
            yield return this[i];
        }
    }

    readonly IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable<Handle<Material>>)this).GetEnumerator();
}
