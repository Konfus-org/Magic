using System.Numerics;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Contexts.ECS;

namespace Magic.SteamAudio;

/// <summary>
/// A sound as the gem follows it: the component as the gem last wrote it back, how far in it is, and the slot the
/// hearing has it heard in, or -1 (<see cref="Hearing"/>).
/// </summary>
internal sealed class Voice
{
    internal Entity Entity { get; init; }

    /// <summary>
    /// The component as the gem last wrote it back, which what a script writes next is told from.
    /// </summary>
    internal Sound Sound { get; set; }

    internal Audio? Audio { get; set; }

    internal int Slot { get; set; } = -1;

    /// <summary>
    /// Which start of its slot it is, which the audio thread's word back names.
    /// </summary>
    internal int Generation { get; set; }

    /// <summary>
    /// Seconds into its sound, as the gem knows it.
    /// </summary>
    internal double TimeSec { get; set; }

    /// <summary>
    /// Its time was set other than by playing, so the audio thread is to be told to play from there.
    /// </summary>
    internal bool IsSeeking { get; set; }

    /// <summary>
    /// The audio thread said it ended of itself, which following the sounds this frame writes back as stopped.
    /// </summary>
    internal bool HasEnded { get; set; }

    internal Vector3 Position { get; set; }

    internal Quaternion Rotation { get; set; }

    internal float Distance { get; set; }

    /// <summary>
    /// The frame it was last seen in: one not seen this frame is of an entity gone.
    /// </summary>
    internal ulong Seen { get; set; }

    /// <summary>
    /// The frame it was last among the nearest that are heard.
    /// </summary>
    internal ulong Chosen { get; set; }
}
