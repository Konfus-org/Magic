using Magic.Services;

namespace Magic.Contexts.Assets;

/// <summary>
/// What opening a domain (<see cref="World.OpenAsync"/>) does to the domains already open.
/// </summary>
public enum DomainOpenMode : byte
{
    /// <summary>
    /// Close them: the world is this domain now, shown once it is there, the loading domain until then.
    /// </summary>
    Replace,

    /// <summary>
    /// Keep them: the domain opens beside them, and appears once its prefabs are in, while the world goes on.
    /// </summary>
    Additive,
}
