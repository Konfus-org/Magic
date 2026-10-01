using System.Runtime.CompilerServices;

namespace Magic.Contexts.Components;

/// <summary>
/// A label with no data: an int, so comparing two costs nothing. Make one from a name with <see cref="Of"/>
/// (a stable hash, the same across runs and machines) and keep it in a static field; the well-known ones
/// live here. The zero tag is "none".
/// </summary>
public readonly record struct Tag(int Id)
{
    public static readonly Tag None = default;

    /// <summary>Never moves: its world transform is computed once and the renderer uploads it once.</summary>
    public static readonly Tag Static = Of("static");

    public bool IsValid => Id != 0;

    /// <summary>FNV-1a over the UTF-16 code units; 0 is reserved, so a name that hashes to it gets 1.</summary>
    public static Tag Of(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        uint hash = 2166136261u;
        foreach (char c in name)
        {
            hash ^= c;
            hash *= 16777619u;
        }

        int id = unchecked((int)hash);

        return new Tag(id == 0 ? 1 : id);
    }
}

/// <summary>
/// The tags an entity carries, inline in one component so it stays plain unmanaged data: up to
/// <see cref="Capacity"/> of them, unordered, no duplicates. Index it like an array or iterate it as a span.
/// </summary>
[InlineArray(Capacity)]
public struct Tags
{
    public const int Capacity = 8;

    private Tag _tag0;

    public readonly bool Has(Tag tag)
    {
        if (!tag.IsValid)
            return false;

        foreach (Tag t in this)
        {
            if (t == tag)
                return true;
        }

        return false;
    }

    /// <summary>Adds the tag; false when it is already there or the container is full.</summary>
    public bool Add(Tag tag)
    {
        if (!tag.IsValid || Has(tag))
            return false;

        for (int i = 0; i < Capacity; i++)
        {
            if (this[i].IsValid)
                continue;

            this[i] = tag;
            return true;
        }

        return false;
    }

    public bool Remove(Tag tag)
    {
        if (!tag.IsValid)
            return false;

        for (int i = 0; i < Capacity; i++)
        {
            if (this[i] != tag)
                continue;

            this[i] = Tag.None;
            return true;
        }

        return false;
    }

    public static Tags Of(params ReadOnlySpan<Tag> tags)
    {
        Tags result = default;
        foreach (Tag tag in tags)
            result.Add(tag);

        return result;
    }
}
