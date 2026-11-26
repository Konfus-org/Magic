using System.ComponentModel;
using Magic.Configuration;

namespace Magic.LightProbes;

/// <summary>
/// How the sky's light is made.
/// </summary>
[Category("Rendering")]
public sealed class LightProbesSettings : ISettings
{
    /// <summary>
    /// The texels along each side of the octahedral map of the sky's radiance, a power of two from 16 to 1024.
    /// </summary>
    public int SkyMapResolution { get; set; } = 256;

    /// <summary>
    /// Whether the sky's light is dimmed by how much of the sky each place sees; off, every place sees all of it.
    /// </summary>
    public bool IsSkyVisibilityOn { get; set; } = true;

    /// <summary>
    /// The directions of the near maps, which test each pixel near the camera.
    /// </summary>
    public int NearDirections { get; set; } = 32;

    /// <summary>
    /// The directions of the far maps, which test the far probes.
    /// </summary>
    public int FarDirections { get; set; } = 16;

    /// <summary>
    /// The metres along each side of the box around the camera that the near maps hold.
    /// </summary>
    public float NearRegion { get; set; } = 128;

    /// <summary>
    /// The metres along each side of the box around the camera that the far maps hold.
    /// </summary>
    public float FarRegion { get; set; } = 512;

    /// <summary>
    /// The texels along each side of each direction map, a power of two from 64 to 2048.
    /// </summary>
    public int DirectionMapResolution { get; set; } = 1024;

    /// <summary>
    /// The metres a pixel's point moves along its normal before the near maps test it. A wall thinner than twice this
    /// can leak.
    /// </summary>
    public float SkyNormalOffset { get; set; } = 0.25f;

    /// <summary>
    /// The maps a frame whose part that a change to the world touched is drawn again, near and far together.
    /// </summary>
    public int ChangeRectsPerFrame { get; set; } = 12;

    /// <summary>
    /// The whole maps of each set a frame that are drawn again when the camera moves their box.
    /// </summary>
    public int ScrollMapsPerFrame { get; set; } = 4;
}
