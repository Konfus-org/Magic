using System.ComponentModel;
using Magic.Configuration;

namespace Magic.ClusteredGeometry;

/// <summary>
/// When a model draws as its impostor instead of its clusters.
/// </summary>
[Category("Rendering")]
public sealed class ClusteredGeometrySettings : ISettings
{
    /// <summary>
    /// The radius on screen, in pixels, under which a model draws as its impostor. Up to 32, half an atlas view, an
    /// impostor is never magnified. 0 or less turns the test off.
    /// </summary>
    public float ImpostorPixels { get; set; } = 16;

    /// <summary>
    /// The distance in metres past which a model draws as its impostor, however large on screen. 0 or less turns the
    /// test off.
    /// </summary>
    public float ImpostorDistance { get; set; } = 1000;
}
