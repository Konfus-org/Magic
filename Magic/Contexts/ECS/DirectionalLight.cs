using Magic.Mathematics;

namespace Magic.Contexts.Components;

/// <summary>
/// Light from infinitely far away (the sun), shining along its entity's +Z. <see cref="Color"/> is linear and
/// <see cref="Intensity"/> scales it. Only one directional light may cast shadows.
/// </summary>
public struct DirectionalLight : IComponent
{
    public DirectionalLight(Color color, float intensity, bool castsShadows = true)
    {
        Color = color;
        Intensity = intensity;
        CastsShadows = castsShadows;
    }

    public Color Color { get; set; }

    public float Intensity { get; set; }

    public bool CastsShadows { get; set; }
}
