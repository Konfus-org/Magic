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
}
