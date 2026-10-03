namespace Magic.Contexts;

/// <summary>
/// What opening a domain does to the ones already open in the world.
/// </summary>
public enum OpenMode
{
    /// <summary>
    /// Close every open domain first.
    /// </summary>
    Replace,

    /// <summary>
    /// Keep them; the domain opens on top.
    /// </summary>
    Additive,
}
