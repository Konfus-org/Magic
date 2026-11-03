namespace Magic.Ecs.Components;

/// <summary>
/// A label with no data: a hash of its name, so comparing two costs nothing and a file can name one no code declares.
/// Made from a name with <see cref="From"/>, the same on every run and machine. An entity holds its tags in a
/// <see cref="Tags"/>.
/// </summary>
public readonly record struct Tag(int Id)
{
    public static Tag None => default;

    /// <summary>
    /// Where a domain's player goes.
    /// </summary>
    public static Tag Spawn { get; } = From("spawn");

    /// <summary>
    /// The player.
    /// </summary>
    public static Tag Player { get; } = From("player");

    /// <summary>
    /// Outlives every domain that replaces the world.
    /// </summary>
    public static Tag Global { get; } = From("global");

    /// <summary>
    /// Goes on while the world is paused, for a gem that reads it.
    /// </summary>
    public static Tag Unpausable { get; } = From("unpausable");

    public bool IsValid => Id != 0;

    /// <summary>
    /// The tag named <paramref name="name"/>: FNV-1a over its UTF-16 code units, and 1 for a name that hashes to 0,
    /// which is <see cref="None"/>.
    /// </summary>
    public static Tag From(string name)
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
