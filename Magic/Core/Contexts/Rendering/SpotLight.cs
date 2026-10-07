using Magic.Mathematics;

namespace Magic.Contexts.Components;

/// <summary>
/// A cone of light along its entity's +Z: full intensity inside <see cref="InnerAngle"/> (the full cone angle),
/// falling to nothing at <see cref="OuterAngle"/>, both in radians. Nothing beyond <see cref="Range"/> metres is lit.
/// </summary>
public struct SpotLight : IComponent
{
    public Color Color { get; set; }

    public float Intensity { get; set; }

    public float Range { get; set; }

    public float InnerAngle { get; set; }

    public float OuterAngle { get; set; }

    public bool CastsShadows { get; set; }
}
