using Magic.Assets;

namespace Magic.Ecs.Components;

/// <summary>
/// What the sky looks like: the material that draws it, whose shader is a sky, not a surface, lit by the sun.
/// </summary>
/// <remarks>
/// With no material, the part that draws uses a background of its own. A world has one sky, on an entity of its own.
/// </remarks>
public record struct Sky : IComponent
{
    public Handle<Material> Material;

    /// <summary>
    /// What the sky's light is multiplied by, from 0: the sky that is seen, the light it gives the surfaces and what
    /// the haze scatters. 1 is the sky of the air itself. The sun and the moon keep their light.
    /// </summary>
    public float Brightness = 1;

    public Sky() { }
}
