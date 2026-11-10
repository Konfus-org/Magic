using System.ComponentModel;

namespace Magic.ClusteredGeometry;

/// <summary>
/// When a model draws as its impostor instead of its clusters.
/// </summary>
[Category("Rendering")]
public sealed record ClusteredGeometrySettings
{
    /// <summary>
    /// The radius on screen, in pixels, under which a model draws as its impostor. 32 is half an atlas view, so an
    /// impostor is never magnified. 0 or less turns the test off.
    /// </summary>
    public float ImpostorPixels { get; init; } = 32;

    /// <summary>
    /// The distance in metres past which a model draws as its impostor, however large on screen. 0 or less turns the
    /// test off.
    /// </summary>
    public float ImpostorDistance { get; init; } = 1000;
}
