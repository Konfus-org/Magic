using Magic.Contexts.Assets;
using Magic.Interfaces;

namespace Magic.Contexts.Components;

/// <summary>
/// Light from infinitely far away, like the sun's, shining along its entity's +Z. <see cref="Intensity"/> scales
/// <see cref="Color"/>. The one tagged <see cref="ECS.Tag.Sun"/> is the world's sun, which lights it and is drawn in
/// its sky, in its colour and along its direction.
/// </summary>
public record struct DirectionalLight : IComponent
{
    public Color Color;

    public float Intensity;
}
