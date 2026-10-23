using System.Diagnostics.CodeAnalysis;
using Magic.Contexts.Assets;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.Contexts.ECS;

/// <summary>
/// What streams into the world as it stands this frame (<see cref="World.Stream"/>), the same all frame: where the world
/// is in having something take its place (<see cref="State"/>), what its domains spawn, and what of that is still taken
/// in before it spawns. Core works it out as each frame begins and tells it on <see cref="Events"/> when its state or
/// its lists change, heard that frame, for a gem the world is built on, which cannot take the world. Every gem that
/// prepares what spawns (<see cref="IPreloader"/>) reads it rather than keeping what it was asked, and what runs the
/// world holds still while it says so (<see cref="IsHeld"/>).
/// </summary>
/// <param name="State">Where the world is in having something take its place.</param>
/// <param name="Frame">Frames since <paramref name="State"/> began, from 0.</param>
/// <param name="Prefabs">What the world's domains spawn or are about to: those open, those opening, and the loading
/// domain, kept for the run once it is first taken in. What none of them names and nothing alive uses, a gem lets go
/// of as the world empties.</param>
/// <param name="TakingIn">Of <paramref name="Prefabs"/>, those still taken in before they spawn, a domain opening
/// beside the world's among them (<see cref="IPreloader.GetProgress"/>).</param>
/// <param name="Version">Changes whenever <paramref name="Prefabs"/> or <paramref name="TakingIn"/> does, so an answer
/// worked out on another thread can say which it answers.</param>
[SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "What streams into the world, named so on purpose; it is no System.IO.Stream, and nothing reads "
        + "it as one.")]
public readonly record struct WorldStream(
    StreamState State,
    int Frame,
    IReadOnlyList<Prefab> Prefabs,
    IReadOnlyList<Prefab> TakingIn,
    int Version)
{
    /// <summary>
    /// A world with nothing streaming in and no domain: as it is before the first frame, or with no gem that provides
    /// the ECS.
    /// </summary>
    public static WorldStream None { get; } = new(StreamState.Loaded, 0, [], [], 0);

    /// <summary>
    /// The world emptied for a new one this frame, as <see cref="StreamState.Loading"/> or
    /// <see cref="StreamState.Readying"/> began: what a gem built up from the old world, and holds for it alone, it
    /// forgets now, so none of it carries over.
    /// </summary>
    public bool IsEmptied => Frame == 0 && State is StreamState.Loading or StreamState.Readying;

    /// <summary>
    /// What runs the world holds still: no step is taken and no sound plays or has its time run, from the frame what
    /// replaces the world spawns out of sight until the frame after it is first shown, so the frame that shows it draws
    /// it as it was readied.
    /// </summary>
    public bool IsHeld => State is StreamState.Readying or StreamState.Shown;
}
