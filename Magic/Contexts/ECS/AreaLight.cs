using Magic.Mathematics;

namespace Magic.Contexts.Components;

/// <summary>
/// A glowing rectangle, <see cref="Width"/> along its entity's +X by <see cref="Height"/> along its +Y (metres, the
/// entity's scale left out), shining from its front, along +Z, and not from behind. A surface takes its light from
/// the part of the rectangle nearest it, and a highlight from the part its reflection sees, so a wide panel lights a
/// wide patch and shows as a shape in a glossy floor, not a dot. Nothing beyond <see cref="Range"/> metres of the
/// rectangle is lit. Its shadow, when it casts one, is the one a light at its middle would cast.
/// </summary>
public struct AreaLight : IComponent
{
    public AreaLight(Color color, float intensity, float range, float width, float height, bool castsShadows = false)
    {
        Color = color;
        Intensity = intensity;
        Range = range;
        Width = width;
        Height = height;
        CastsShadows = castsShadows;
    }

    public Color Color { get; set; }

    public float Intensity { get; set; }

    public float Range { get; set; }

    public float Width { get; set; }

    public float Height { get; set; }

    public bool CastsShadows { get; set; }
}
