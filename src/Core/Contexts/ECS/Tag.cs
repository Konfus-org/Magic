using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Magic.Contexts.Assets;
using Magic.Interfaces;
using Magic.Internal;
using Magic.Services;

namespace Magic.Contexts.ECS;

/// <summary>
/// A label with no data: a hash of its name, so comparing two costs nothing and a file can name one no code declares.
/// Made from a name with <see cref="Of"/>, the same on every run and machine; the zero tag is none. The engine's own are
/// here.
/// </summary>
public readonly record struct Tag(int Id)
{
    /// <summary>
    /// Where the project's player is put when a domain opens in place of the world (<see cref="World.OpenAsync"/>): the
    /// first entity tagged so in the domain's prefabs that are not cells, else in its cells.
    /// </summary>
    public static readonly Tag Spawn = Of("spawn");

    /// <summary>
    /// The player's camera, which <see cref="Project.Player"/> spawns: what scripts are run more often near, and detail
    /// is kept nearest to.
    /// </summary>
    public static readonly Tag Player = Of("player");

    public bool IsValid => Id != 0;

    /// <summary>
    /// The tag named <paramref name="name"/>: FNV-1a over its UTF-16 code units, and 1 for a name that hashes to 0, which
    /// is none.
    /// </summary>
    public static Tag Of(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        uint hash = 2166136261u;
        foreach (char character in name)
        {
            hash ^= character;
            hash = unchecked(hash * 16777619u);
        }

        int id = unchecked((int)hash);
        return new Tag(id == 0 ? 1 : id);
    }
}

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
