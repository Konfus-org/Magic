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
    /// The finished picture's size in pixels, with a window or without, which the window shows stretched over it. 0 by
    /// 0 is auto: the window's size, or 1280 by 720 with no window, as it is with a side of 0 or less.
    /// </summary>
    public Size Resolution { get; init; }

    /// <summary>
    /// The part of the resolution the world is drawn at, more than 0 to 1, before the picture is upscaled or stretched
    /// to it. 0 is auto: the most that draws at 1920 by 1080 or less.
    /// </summary>
    public float RenderScale { get; init; }

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

    /// <summary>
    /// How many of a texture's largest mip levels are left off the GPU, each halving its width and height and saving
    /// about three quarters of its memory: a softer picture for less video memory. A texture keeps at least its smallest
    /// level. 0 keeps every level.
    /// </summary>
    [RequireRestart]
    public int DroppedTextureLevels { get; init; }
}
