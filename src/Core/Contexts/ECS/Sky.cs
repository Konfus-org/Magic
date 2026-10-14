using Magic.Contexts.Assets;
using Magic.Interfaces;

namespace Magic.Contexts.ECS;

/// <summary>
/// What the sky looks like: the material it is drawn with, <c>{ "material": { "id": 710 } }</c>; none leaves it to
/// whatever draws, which has a background of its own. A world has one, on an entity of its own.
/// </summary>
public record struct Sky : IComponent
{
    public Handle<Material> Material;
}
