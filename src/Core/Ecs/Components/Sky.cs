using Magic.Files;
using Magic.Files.Types;

namespace Magic.Ecs.Components;

/// <summary>
/// What the sky looks like: the material it is drawn with, whose shader is a sky rather than a surface, lit by the
/// world's sun; none leaves it to whatever draws, which has a background of its own. A world has one, on an entity of
/// its own.
/// </summary>
public record struct Sky : IComponent
{
    public Handle<Material> Material;
}
