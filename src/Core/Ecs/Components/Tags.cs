using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Magic.Ecs.Components;

/// <summary>
/// The tags of an entity, inline so the component stays plain unmanaged data.
/// </summary>
/// <remarks>
/// It holds up to <see cref="Capacity"/> tags, in no order, each one time. A free slot holds <see cref="Tag.None"/>.
/// </remarks>
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
        // The other has each tag that this has, and no more tags than this.
        int count = 0;
        foreach (Tag held in this)
        {
            if (!held.IsValid)
                continue;

            if (!other.Has(held))
                return false;

            count++;
        }

        foreach (Tag held in other)
        {
            if (held.IsValid)
                count--;
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
        Span<Tag> held = this;
        int free = held.IndexOf(Tag.None);
        if (!tag.IsValid || held.Contains(tag) || free < 0)
            return false;

        held[free] = tag;

        return true;
    }
}
