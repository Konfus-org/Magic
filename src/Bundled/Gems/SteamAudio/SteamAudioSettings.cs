using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Magic.Attributes;
using Magic.Interfaces;

namespace Magic.SteamAudio;

/// <summary>
/// How sounds are played and traced. Set in the project's <c>Presets/Default.preset</c>, by another preset, or with
/// <c>--set SteamAudio.volume=0.5</c>.
/// </summary>
[Category("Audio")]
internal sealed class SteamAudioSettings : ISettings
{
    /// <summary>
    /// How loud everything is, on top of each sound's own volume: 0 is silence.
    /// </summary>
    [Range(0.0, 2.0)]
    public float Volume { get; set; } = 1;

    /// <summary>
    /// Sample frames a second the device is fed at; a sound at another rate is converted as it plays.
    /// </summary>
    [RequireRestart]
    [Range(22050, 96000)]
    public int SampleRate { get; set; } = 48000;

    /// <summary>
    /// Sample frames worked out at a time: fewer answer sooner, more cost less time each.
    /// </summary>
    [RequireRestart]
    [Range(256, 4096)]
    public int FrameSize { get; set; } = 1024;

    /// <summary>
    /// Sounds heard at once; past it the farthest in the world go quiet, each going on in silence as if heard, until
    /// it is among the nearest again.
    /// </summary>
    [RequireRestart]
    [Range(1, 256)]
    public int Voices { get; set; } = 64;

    /// <summary>
    /// Sounds echo off what is solid around them, traced through the world on a thread of the gem's own.
    /// </summary>
    public bool HasReflections { get; set; } = true;

    /// <summary>
    /// How many of the nearest sounds in the world echo; each holds its echo, a few MB, for as long as the run.
    /// </summary>
    [RequireRestart]
    [Range(0, 32)]
    public int ReflectionSources { get; set; } = 8;

    /// <summary>
    /// Rays traced from the listener for each trace of the echoes: more make a smoother echo and cost as much more.
    /// </summary>
    [RequireRestart]
    [Range(256, 32768)]
    public int Rays { get; set; } = 4096;

    /// <summary>
    /// How many times a ray bounces before it is let go: more hear deeper into rooms and corridors.
    /// </summary>
    [Range(1, 64)]
    public int Bounces { get; set; } = 16;

    /// <summary>
    /// Seconds an echo can ring for: a cathedral needs more, and each holds as much more memory.
    /// </summary>
    [RequireRestart]
    [Range(0.1, 10.0)]
    public float ReverbSeconds { get; set; } = 2;

    /// <summary>
    /// How finely an echo keeps where it comes from: 0 hears it from everywhere alike, each step above as many more
    /// directions, at four, nine, sixteen times the cost of the first.
    /// </summary>
    [RequireRestart]
    [Range(0, 3)]
    public int Order { get; set; } = 1;

    /// <summary>
    /// Times a second the echoes are traced again, as the listener and the sounds move.
    /// </summary>
    [Range(1.0, 60.0)]
    public float ReflectionRate { get; set; } = 10;

    /// <summary>
    /// Metres from the player's camera within which what is solid muffles and echoes sound; past it nothing is traced
    /// against. With no player camera, everything solid is.
    /// </summary>
    public float Range { get; set; } = 200;
}
