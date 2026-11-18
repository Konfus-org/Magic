using Magic.Projects;

namespace Magic.SamplesShared;

/// <summary>
/// The gem, which brings its assets and the day that the console holds: a project that lists it finds the assets as
/// its own.
/// </summary>
public sealed class EntryPoint : IGem
{
    /// <summary>
    /// The hour that the console holds each day cycle at. Game thread.
    /// </summary>
    public Day Day { get; } = new();
}
