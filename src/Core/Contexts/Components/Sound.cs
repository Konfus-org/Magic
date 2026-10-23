using Magic.Contexts.Assets;
using Magic.Interfaces;

namespace Magic.Contexts.Components;

/// <summary>
/// Whether a <see cref="Sound"/> plays.
/// </summary>
public enum Playback : byte
{
    Playing,

    /// <summary>
    /// Holds where it is, to go on from there when playing again.
    /// </summary>
    Paused,

    /// <summary>
    /// Silent, at the start: what a sound that does not loop is once it ends.
    /// </summary>
    Stopped,
}

/// <summary>
/// Where a <see cref="Sound"/> is heard from.
/// </summary>
public enum SoundSpace : byte
{
    /// <summary>
    /// From its entity's place in the world: from the side it is on, quieter the farther it is, muffled by what is
    /// solid between it and the listener and echoed by what is around them. Played mono, as one point sounds.
    /// </summary>
    World,

    /// <summary>
    /// Everywhere alike, as it was recorded, stereo staying stereo: music, ambience, what the player hears in their
    /// head.
    /// </summary>
    Everywhere,
}

/// <summary>
/// Plays an <see cref="Assets.Audio"/>: in the world at its entity's <see cref="Transform"/>, heard from the player's
/// camera, fading with distance and muffled and echoed by what is solid around it, or everywhere alike, for music and
/// ambience. A zero reads as its default, so <c>{ "audio": { "id": 800 } }</c> in a file plays the sound once, at full
/// volume, in the world, heard at full volume within a metre and not at all past 25; <c>"loop": true</c> plays it
/// without end, and <c>"space": "everywhere"</c> plays it as music is. The gem that plays sounds writes back how far in
/// it is (<see cref="TimeSec"/>) and when a sound that does not loop has ended (<see cref="Playback"/>), and follows
/// what a script writes: <see cref="Playback"/> to play, pause or stop it, <see cref="TimeSec"/> to seek, and every
/// other value as it changes.
/// </summary>
public record struct Sound : IComponent
{
    /// <summary>
    /// What it plays; none plays nothing.
    /// </summary>
    public Handle<Audio> Audio { get; set; }

    /// <summary>
    /// Whether it plays: playing by default, so a sound placed in the world is heard as it spawns. Set to playing again
    /// after it stopped, it plays from <see cref="TimeSec"/>, which stopping put back to the start.
    /// </summary>
    public Playback Playback { get; set; }

    /// <summary>
    /// It starts again from the start each time it ends, never stopping of itself.
    /// </summary>
    public bool IsLooping { get; set; }

    /// <summary>
    /// How loud, from just above 0 to 1 and beyond; silence is <see cref="Playback.Paused"/>.
    /// </summary>
    public float Volume
    {
        readonly get => field == 0 ? 1 : field;
        set;
    }

    /// <summary>
    /// How fast it plays, raising its pitch as it goes faster: 2 is an octave up and twice as fast, 0.5 an octave down.
    /// Held between the two.
    /// </summary>
    public float Pitch
    {
        readonly get => field == 0 ? 1 : field;
        set;
    }

    /// <summary>
    /// Where it is heard from: the world, at its entity, or everywhere alike.
    /// </summary>
    public SoundSpace Space { get; set; }

    /// <summary>
    /// Metres within which it is heard at full volume, fading with distance past it.
    /// </summary>
    public float Near
    {
        readonly get => field == 0 ? 1 : field;
        set;
    }

    /// <summary>
    /// Metres past which it is not heard, and costs nothing: it fades out on the way there, however loud.
    /// </summary>
    public float Range
    {
        readonly get => field == 0 ? 25 : field;
        set;
    }

    /// <summary>
    /// How much it is heard only ahead of it, along its entity's +Z: 0 every way alike, 1 ahead alone, as a
    /// loudspeaker or a voice is, from 0.3 or so.
    /// </summary>
    public float Focus { get; set; }

    /// <summary>
    /// What is solid between it and the listener does not muffle it, which costs nothing to trace: for a sound meant
    /// to be heard whatever is in the way.
    /// </summary>
    public bool IsUnoccluded { get; set; }

    /// <summary>
    /// It does not echo off what is around it, which costs nothing to trace.
    /// </summary>
    public bool IsDry { get; set; }

    /// <summary>
    /// Seconds into it, at its own pitch, as it was last heard. Set it to play from there.
    /// </summary>
    public float TimeSec { get; set; }
}
