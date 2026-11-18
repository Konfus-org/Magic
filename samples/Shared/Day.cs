using Magic.CommandLine;

namespace Magic.SamplesShared;

/// <summary>
/// The hour that the console holds the day at.
/// </summary>
/// <remarks>
/// <c>set Day.Hour 12</c> holds each day cycle of the world at noon. It is state of the run and is not saved. Game
/// thread.
/// </remarks>
[CliPath("Day")]
public sealed class Day
{
    /// <summary>
    /// The hour of the sun, from 0 to 24, that each day cycle stays at; null while each runs at its own speed.
    /// </summary>
    public float? Hour { get; set; }
}
