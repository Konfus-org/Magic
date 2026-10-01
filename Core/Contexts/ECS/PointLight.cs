using System.Numerics;

namespace Magic.Contexts.Components;

/// <summary>
/// Light radiating from its entity's position. <see cref="Color"/> is linear and <see cref="Intensity"/> scales
/// it; nothing beyond <see cref="Range"/> metres is lit, so the renderer can cull it.
/// </summary>
public struct PointLight : IComponent
{
    public PointLight(Vector3 color, float intensity, float range, bool castsShadows = false)
    {
        Color = color;
        Intensity = intensity;
        Range = range;
        CastsShadows = castsShadows;
    }

    public Vector3 Color { get; set; }

    public float Intensity { get; set; }

    public float Range { get; set; }

    public bool CastsShadows { get; set; }
}
