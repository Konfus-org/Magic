using Magic.Interfaces;
using Magic.Services;

namespace Magic.Contexts.ECS;

/// <summary>
/// Where the world is in having something stream in to take its place (<see cref="WorldStream.State"/>): a domain
/// opening in place of it (<see cref="Assets.DomainOpenMode.Replace"/>), or a saved world put back
/// (<see cref="World.Restore"/>). The world empties as <see cref="Loading"/> and <see cref="Readying"/> begin.
/// </summary>
public enum StreamState : byte
{
    /// <summary>
    /// Nothing takes the world's place: it is shown, and runs.
    /// </summary>
    Loaded,

    /// <summary>
    /// A domain is taken in to replace the world while the loading domain shows, and runs.
    /// </summary>
    Loading,

    /// <summary>
    /// What replaces the world is spawned out of sight while every gem readies it (<see cref="IPreloader.IsReady"/>),
    /// the window keeping the loading domain's last image; what runs the world holds still. A saved world put back is
    /// readied from the frame it is put back.
    /// </summary>
    Readying,

    /// <summary>
    /// The world is shown for the first time this frame, drawn as it was readied; what runs it holds still this one
    /// frame more, and runs from the next.
    /// </summary>
    Shown,
}
