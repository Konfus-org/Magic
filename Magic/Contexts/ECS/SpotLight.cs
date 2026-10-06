using Magic.Mathematics;

namespace Magic.Contexts.Components;

/// <summary>
/// A cone of light along its entity's +Z: full intensity inside <see cref="InnerAngle"/> (the full cone angle),
/// falling to nothing at <see cref="OuterAngle"/>, both in radians because that is what the shader wants; the
/// constructor takes degrees because that is how people write them. Nothing beyond <see cref="Range"/> metres is lit.
/// </summary>
public struct SpotLight : IComponent
{
    public SpotLight(Color color, float intensity, float range, float innerDegrees, float outerDegrees, bool castsShadows = false)
    {
        Color = color;
        Intensity = intensity;
        Range = range;
        InnerAngle = float.DegreesToRadians(innerDegrees);
        OuterAngle = float.DegreesToRadians(outerDegrees);
        CastsShadows = castsShadows;
    }

    public Color Color { get; set; }

    public float Intensity { get; set; }

    public float Range { get; set; }

    public float InnerAngle { get; set; }

    public float OuterAngle { get; set; }

    public bool CastsShadows { get; set; }
}
