using System.ComponentModel;
using System.Drawing;
using Magic.Configuration;

namespace Magic.GpuWorld;

/// <summary>
/// How the world is drawn.
/// </summary>
[Category("Rendering")]
public sealed record GpuWorldSettings
{
    /// <summary>
    /// The most pixels across and down the picture is drawn at, in the window's shape. A smaller window draws at its
    /// own size. 0 by 0 is auto: the picture is drawn at the window's size, as it is with a side of 0 or less.
    /// </summary>
    public Size Resolution { get; init; } = new(1920, 1080);

    /// <summary>
    /// The window camera's vertical field of view in degrees, in place of the camera's own. 0 or less keeps the
    /// camera's.
    /// </summary>
    public float FieldOfView { get; init; }

    /// <summary>
    /// How bright the open sky's light is, as a part of the sun's: what lights a world with no sky entity and what fog
    /// scatters.
    /// </summary>
    public float SkyBrightness { get; init; } = 0.3f;

    /// <summary>
    /// How many seconds a model nothing draws or waits for stays on the GPU before it leaves, to load again when it is
    /// next drawn.
    /// </summary>
    public int ModelKeepSec { get; init; } = 60;

    /// <summary>
    /// How many samples a smoothly filtered texture takes along a surface seen at a slant, 1 to 16: more keeps a floor
    /// sharp into the distance. 1 is off.
    /// </summary>
    [RequireRestart]
    public int Anisotropy { get; init; } = 8;
}
