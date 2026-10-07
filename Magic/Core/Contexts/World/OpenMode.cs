namespace Magic.Contexts.Domain;

/// <summary>
/// What opening a domain does to the ones already open in the world.
/// </summary>
public enum OpenMode
{
    /// <summary>
    /// Close every open domain first (but overlays): the world is this domain now. It is not shown, and its scripts
    /// wait, until it is loaded; the world is <see cref="DomainState.Loading"/> until then.
    /// </summary>
    Replace,

    /// <summary>
    /// Keep them; the domain opens on top, and is shown as it fills.
    /// </summary>
    Additive,

    /// <summary>
    /// Keep them, and stay open when they are replaced: shown over the world without being part of it (a loading
    /// screen). It counts for nothing in <see cref="Services.World.State"/>, and a screenshot does not save it.
    /// </summary>
    Overlay,
}
