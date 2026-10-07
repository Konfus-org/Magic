using Magic.Mathematics;

namespace Magic.Contexts.Components;

/// <summary>
/// Light radiating from its entity's position. <see cref="Color"/> is linear and <see cref="Intensity"/> scales
/// it; nothing beyond <see cref="Range"/> metres is lit, so the renderer can cull it.
/// </summary>
public struct PointLight : IComponent
{
    public Color Color { get; set; }

    public float Intensity { get; set; }

    public float Range { get; set; }

    public bool CastsShadows { get; set; }
}
