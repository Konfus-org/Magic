namespace Magic.Contexts;

/// <summary>
/// How far a domain is in the world.
/// </summary>
public enum DomainState
{
    /// <summary>
    /// Not open.
    /// </summary>
    Closed,

    /// <summary>
    /// Open, and still being filled in: its globals or the chunks its cameras want are not all there.
    /// </summary>
    Loading,

    /// <summary>
    /// Open and there: its globals and the chunks its cameras wanted are spawned. Chunks stream in and out from here on.
    /// </summary>
    Loaded,
}
