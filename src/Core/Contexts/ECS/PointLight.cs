using Magic.Contexts.Assets;
using Magic.Interfaces;

namespace Magic.Contexts.ECS;

/// <summary>
/// Light from a small sphere at its entity's position, shining every way. A zero reads as its default, so
/// <c>new PointLight()</c>, or <c>{}</c> in a file, is a white 800 lumen bulb, 5 cm across, reaching 10 m.
/// </summary>
public record struct PointLight : IComponent
{
    public Color Color
    {
        readonly get => field == default ? new Color(1, 1, 1) : field;
        set;
    }

    /// <summary>
    /// Luminous intensity in candela, lumens / 4π: an 800 lumen bulb is 64.
    /// </summary>
    public float Intensity
    {
        readonly get => field == 0 ? 64 : field;
        set;
    }

    /// <summary>
    /// Metres it reaches: its light fades to nothing there, so whatever draws it can leave it out of everything farther.
    /// </summary>
    public float Range
    {
        readonly get => field == 0 ? 10 : field;
        set;
    }

    /// <summary>
    /// Metres from the bulb's centre to its surface, which widens its highlights as a real bulb's are.
    /// </summary>
    public float Radius
    {
        readonly get => field == 0 ? 0.05f : field;
        set;
    }

    /// <summary>
    /// It casts no shadow: its light reaches through everything, which costs next to nothing to draw. A light far off,
    /// whose shadows would not show, is best drawn so.
    /// </summary>
    public bool Shadowless { get; set; }
}
