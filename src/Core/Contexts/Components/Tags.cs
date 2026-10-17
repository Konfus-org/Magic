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
        int count = 0;
        foreach (Tag held in this)
        {
            if (!held.IsValid)
            {
                continue;
            }

            if (!other.Has(held))
            {
                return false;
            }

            count++;
        }

        foreach (Tag held in other)
        {
            if (held.IsValid)
            {
                count--;
            }
        }

        return count == 0;
    }

    public override readonly bool Equals(object? obj) => obj is Tags other && Equals(other);

    public override readonly int GetHashCode()
    {
        int hash = 0;
        foreach (Tag held in this)
        {
            hash ^= held.Id;
        }

        return hash;
    }

    public readonly bool Has(Tag tag)
    {
        if (!tag.IsValid)
        {
            return false;
        }

        foreach (Tag held in this)
        {
            if (held == tag)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Adds <paramref name="tag"/>; false when it is none, there already, or there is no room for it.
    /// </summary>
    public bool Add(Tag tag)
    {
        if (!tag.IsValid || Has(tag))
        {
            return false;
        }

        for (int i = 0; i < Capacity; i++)
        {
            if (!this[i].IsValid)
            {
                this[i] = tag;
                return true;
            }
        }

        return false;
    }
}
