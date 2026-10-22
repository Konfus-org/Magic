using Magic.Contexts.Assets;
using Magic.Services;

namespace Magic.Contexts.ECS;

/// <summary>
/// A label with no data: a hash of its name, so comparing two costs nothing and a file can name one no code declares.
/// Made from a name with <see cref="Of"/>, the same on every run and machine; the zero tag is none. The engine's own
/// are here; an entity holds its tags in a <see cref="Components.Tags"/>.
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
    /// The tag named <paramref name="name"/>: FNV-1a over its UTF-16 code units, and 1 for a name that hashes to 0,
    /// which is none.
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
