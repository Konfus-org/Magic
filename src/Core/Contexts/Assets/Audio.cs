using Magic.Interfaces;

namespace Magic.Contexts.Assets;

/// <summary>
/// A recorded sound, decoded whole: its samples as 32-bit floats, interleaved by channel, at the rate and with the
/// channels its file has. Whatever plays it converts it to its device's rate and channels as it plays, so a file is
/// never resampled on load and plays at its own quality; a <see cref="Components.Sound"/> on an entity names one to
/// play.
/// </summary>
public sealed class Audio : IAsset<Audio>
{
    public static IReadOnlyList<string> Extensions { get; } = [".wav", ".ogg", ".mp3", ".flac"];

    public Handle<Audio> Handle { get; init; }

    /// <summary>
    /// Sample frames per second, one sample of every channel each.
    /// </summary>
    public required int SampleRate { get; init; }

    /// <summary>
    /// How many channels each frame holds: 1 mono, 2 stereo.
    /// </summary>
    public required int Channels { get; init; }

    /// <summary>
    /// Every sample, frame by frame, each frame's channels side by side, from -1 to 1.
    /// </summary>
    public required ReadOnlyMemory<float> Samples { get; init; }

    /// <summary>
    /// How long it plays at its own pitch.
    /// </summary>
    public double DurationSec => Channels == 0 || SampleRate == 0 ? 0 : (double)Samples.Length / Channels / SampleRate;
}
