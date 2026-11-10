using System.ComponentModel;
using System.Drawing;

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
}
