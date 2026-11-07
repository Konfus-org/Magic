using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Magic.Files;
using Magic.Files.Types;

namespace Magic.Ecs.Components;

/// <summary>
/// Draws a model where its entity's <see cref="Transform"/> puts it, each of the model's meshes with the material in
/// the slot the mesh names, with the params the slot sets over the material's.
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
/// Materials by slot, as a component lists what it draws with: a <see cref="Renderer"/>'s by the slot a model's mesh
/// names (<see cref="Mesh.MaterialSlot"/>), and the post and weather effects in the order they run.
/// <see cref="Capacity"/> of them, each a <see cref="MaterialInstance"/>, inline so the component stays plain unmanaged
/// data, indexed, an empty slot's handle none. In a file, an array of them, the empty slots after the last left out.
/// </summary>
[InlineArray(Capacity)]
[JsonConverter(typeof(MaterialSlotsJsonConverter))]
public struct MaterialSlots : IEquatable<MaterialSlots>
{
    public const int Capacity = 8;

    private MaterialInstance _first;

    /// <summary>
    /// Every slot as a list, an empty slot's handle none: what reading an inline array through reflection can't
    /// reach.
    /// </summary>
    public readonly ImmutableArray<MaterialInstance> All => [.. (ReadOnlySpan<MaterialInstance>)this];

    public static bool operator ==(MaterialSlots left, MaterialSlots right) => left.Equals(right);

    public static bool operator !=(MaterialSlots left, MaterialSlots right) => !left.Equals(right);

    /// <summary>
    /// Whether both hold the same material, setting the same params, in every slot.
    /// </summary>
    public readonly bool Equals(MaterialSlots other) => ((ReadOnlySpan<MaterialInstance>)this).SequenceEqual(other);

    public override readonly bool Equals(object? obj) => obj is MaterialSlots other && Equals(other);

    public override readonly int GetHashCode()
    {
        HashCode hash = default;
        foreach (MaterialInstance held in this)
        {
            hash.Add(held);
        }

        return hash.ToHashCode();
    }
}
