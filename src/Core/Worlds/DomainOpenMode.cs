namespace Magic.Worlds;

/// <summary>
/// How a domain opens.
/// </summary>
public enum DomainOpenMode : byte
{
    /// <summary>
    /// In place of what is in the world, all but its globals, with the loading screen shown until it is there and the
    /// project's player at its spawn.
    /// </summary>
    Replace,

    /// <summary>
    /// Beside what is in the world.
    /// </summary>
    Additive,
}
