namespace Magic.Ecs.Components;

/// <summary>
/// A label with no data: a hash of its name, so a compare costs nothing and a file can name a tag no code declares.
/// </summary>
/// <remarks>
/// <see cref="From"/> makes one from a name, the same on each run and machine. An entity holds its tags in a
/// <see cref="Tags"/>.
/// </remarks>
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

    public bool IsValid => Id != 0;

    /// <summary>
    /// Returns the tag named <paramref name="name"/>.
    /// </summary>
    /// <remarks>
    /// It is FNV-1a over the UTF-16 code units of the name. A name that hashes to 0, <see cref="None"/>, gets 1.
    /// </remarks>
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
