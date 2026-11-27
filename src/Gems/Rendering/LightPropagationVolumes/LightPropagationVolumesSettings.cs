using System.ComponentModel;
using Magic.Configuration;

namespace Magic.LightPropagationVolumes;

/// <summary>
/// How the sun's bounced light is spread through the grid around the camera.
/// </summary>
[Category("Rendering")]
public sealed class LightPropagationVolumesSettings : ISettings
{
    /// <summary>
    /// Whether the sun's light bounces off the surfaces that it lights onto the rest of the world near the camera.
    /// </summary>
    public bool IsBounceLightOn { get; set; } = true;

    /// <summary>
    /// The metres along each side of a cell of the grid. The grid has 32 cells along each side, so it reaches 16 times
    /// this from the camera. Smaller cells keep the light in small rooms, larger ones reach further.
    /// </summary>
    public float CellSize { get; set; } = 0.5f;

    /// <summary>
    /// How many times a frame the light moves one cell on, 1 to 32: the cells that it reaches from where it bounced.
    /// </summary>
    public int Steps { get; set; } = 8;

    /// <summary>
    /// A scale on the bounced light that surfaces get: 1 is the light that the grid holds.
    /// </summary>
    public float Brightness { get; set; } = 1;
}
