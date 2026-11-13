namespace Magic.Assets;

/// <summary>
/// A recorded sound, decoded whole: 32-bit float samples, interleaved by channel, at the rate and channels of its file.
/// </summary>
/// <remarks>
/// The player converts it to the rate and channels of its device as it plays, so a load never resamples a file.
/// </remarks>
public sealed class Audio : IAsset<Audio>
{
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
