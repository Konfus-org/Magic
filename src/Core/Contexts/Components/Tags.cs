using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Magic.Contexts.ECS;
using Magic.Interfaces;
using Magic.Internal;

namespace Magic.Contexts.Components;

/// <summary>
/// The tags an entity has, inline so the component stays plain unmanaged data: up to <see cref="Capacity"/>, unordered,
/// each once. A file gives an entity its tags by name, beside its components: <c>"tags": [ "spawn" ]</c>. As JSON
/// themselves, as a saved world holds them, they are an array of their ids.
/// </summary>
[InlineArray(Capacity)]
[JsonConverter(typeof(TagsJsonConverter))]
public struct Tags : IComponent, IEquatable<Tags>
{
    public const int Capacity = 8;

    private Tag _first;

    public static bool operator ==(Tags left, Tags right) => left.Equals(right);

    public static bool operator !=(Tags left, Tags right) => !left.Equals(right);

    /// <summary>
    /// Whether both have the same tags, wherever each holds them.
    /// </summary>
    public readonly bool Equals(Tags other)
    {
        // Every tag this has, the other has too, and the other has no more than this.
        int count = 0;
        foreach (Tag held in this)
        {
            if (!held.IsValid) continue;

            if (!other.Has(held)) return false;

            count++;
        }

        foreach (Tag held in other)
        {
            if (held.IsValid) count--;
        }

        return count == 0;
    }

    public override readonly bool Equals(object? obj) => obj is Tags other && Equals(other);

    public override readonly int GetHashCode()
    {
        // In no order, as Equals ignores which slot holds each.
        int hash = 0;
        foreach (Tag held in this)
        {
            hash ^= held.Id;
        }

        return hash;
    }

    public readonly bool Has(Tag tag) => tag.IsValid && ((ReadOnlySpan<Tag>)this).Contains(tag);

    /// <summary>
    /// Adds <paramref name="tag"/>; false when it is none, there already, or there is no room for it.
    /// </summary>
    public bool Add(Tag tag)
    {
        // A slot holding none is free.
        Span<Tag> tags = this;
        int free = tags.IndexOf(default(Tag));

        if (!tag.IsValid || Has(tag) || free < 0) return false;

        tags[free] = tag;

        return true;
    }
}
