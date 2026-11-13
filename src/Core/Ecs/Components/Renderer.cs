using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Magic.Assets;

namespace Magic.Ecs.Components;

/// <summary>
/// Draws a model at the <see cref="Transform"/> of its entity, each mesh with the material in the slot that the mesh
/// names.
/// </summary>
/// <remarks>
/// The params that the slot sets go over those of the material. The component is a few bytes, because a world holds
/// very many: the slots are a shared <see cref="MaterialSet"/>.
/// </remarks>
public record struct Renderer : IComponent
{
    public Handle<Model> Model;

    public MaterialSet Materials;

    /// <summary>
    /// Whether it casts no shadow: it is drawn, but light goes through it, as through the shade of a lamp.
    /// </summary>
    public bool IsShadowless;
}

/// <summary>
/// Materials by slot, inline in a component of which a world holds few, such as the post effects in the order they run.
/// </summary>
/// <remarks>
/// It holds <see cref="Capacity"/> slots, each a <see cref="MaterialInstance"/>, inline so the component stays plain
/// unmanaged data. An empty slot has no handle. A script can change the params each frame:
/// <c>post.Effects[0].Overrides["compensation"] = 1f</c>. In a file, it is an array of instances, without the empty
/// slots after the last.
/// </remarks>
[InlineArray(Capacity)]
[JsonConverter(typeof(MaterialSlotsJsonConverter))]
public struct MaterialSlots : IEquatable<MaterialSlots>
{
    public const int Capacity = 8;

    private MaterialInstance _first;

    /// <summary>
    /// Every slot as a list, where an empty slot has no handle.
    /// </summary>
    /// <remarks>
    /// Reflection cannot read an inline array, so it reads this list.
    /// </remarks>
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
