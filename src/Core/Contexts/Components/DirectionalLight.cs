using Magic.Contexts.Assets;
using Magic.Interfaces;

namespace Magic.Contexts.Components;

/// <summary>
/// Light from infinitely far away, like the sun's, shining along its entity's +Z. <see cref="Intensity"/> scales
/// <see cref="Color"/>.
/// </summary>
public record struct DirectionalLight : IComponent
{
    public Color Color;

    public float Intensity;
}
