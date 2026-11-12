using Magic.Assets;

namespace Magic.Ecs.Components;

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
    /// From the place of its entity in the world, from its side and quieter with distance.
    /// </summary>
    /// <remarks>
    /// Solid things between it and the listener muffle it, and things around them echo it. It plays mono, as one point
    /// sounds.
    /// </remarks>
    World,

    /// <summary>
    /// Everywhere alike, as it was recorded, stereo staying stereo: music, ambience, what the player hears in their
    /// head.
    /// </summary>
    Everywhere,
}

/// <summary>
/// Plays an <see cref="Assets.Audio"/>, in the world at the <see cref="Transform"/> of its entity, or everywhere alike.
/// </summary>
/// <remarks>
/// <para>
/// In the world, the camera of the player hears it. It fades with distance, and solid things around it muffle and
/// echo it. Everywhere alike is for music and ambience.
/// </para>
/// <para>
/// A new one, <c>{ "audio": { "id": 800 } }</c> in a file, plays the sound once, at full volume, in the world. It is
/// heard at full volume within a metre and not at all past 25 m. <c>"isLooping": true</c> plays it without end, and
/// <c>"space": "everywhere"</c> plays it as music.
/// </para>
/// <para>
/// The gem that plays sounds writes back how far in it is (<see cref="TimeSec"/>) and when a sound that does not loop
/// ended (<see cref="Playback"/>). It follows what a script writes: <see cref="Playback"/> to play, pause or stop it,
/// <see cref="TimeSec"/> to seek, and each other value when it changes.
/// </para>
/// </remarks>
public record struct Sound : IComponent
{
    /// <summary>
    /// What it plays; none plays nothing.
    /// </summary>
    public Handle<Audio> Audio;

    /// <summary>
    /// Whether it plays: playing by default, so a sound placed in the world is heard when it spawns.
    /// </summary>
    /// <remarks>
    /// Set to playing after a stop, it plays from <see cref="TimeSec"/>, which the stop put back to the start.
    /// </remarks>
    public Playback Playback;

    /// <summary>
    /// It starts again from the start each time it ends, never stopping of itself.
    /// </summary>
    public bool IsLooping;

    /// <summary>
    /// Where it is heard from: the world, at its entity, or everywhere alike.
    /// </summary>
    public SoundSpace Space;

    /// <summary>
    /// How much it is heard only ahead, along the +Z of its entity: 0 in all directions alike, 1 only ahead.
    /// </summary>
    /// <remarks>
    /// A loudspeaker or a voice is about 0.3.
    /// </remarks>
    public float Focus;

    /// <summary>
    /// Whether solid things between it and the listener do not muffle it, which costs no trace.
    /// </summary>
    /// <remarks>
    /// Use it for a sound that must be heard through anything in the way.
    /// </remarks>
    public bool IsUnoccluded;

    /// <summary>
    /// It does not echo off what is around it, which costs nothing to trace.
    /// </summary>
    public bool IsDry;

    /// <summary>
    /// Seconds into it, at its own pitch, as it was last heard. Set it to play from there.
    /// </summary>
    public float TimeSec;

    /// <summary>
    /// How loud, from 0 to 1 and beyond.
    /// </summary>
    public float Volume = 1;

    /// <summary>
    /// How fast it plays, which raises its pitch as it goes faster: 2 is an octave up, 0.5 an octave down.
    /// </summary>
    /// <remarks>
    /// The value is held between 0.5 and 2.
    /// </remarks>
    public float Pitch = 1;

    /// <summary>
    /// Metres within which it is heard at full volume, fading with distance past it.
    /// </summary>
    public float Near = 1;

    /// <summary>
    /// Metres past which it is not heard, and costs nothing: it fades out on the way there, however loud.
    /// </summary>
    public float Range = 25;

    public Sound() { }
}
