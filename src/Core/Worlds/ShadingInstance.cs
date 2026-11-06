using Magic.Files;
using Magic.Files.Types;

namespace Magic.Worlds;

/// <summary>
/// An entity's own shading over its material's <see cref="Shading"/>: the params it sets in place of the material's,
/// by the names of the shader's fields, <c>instance["color"] = new Color(1, 0, 0)</c>, which whatever draws it reads
/// over the material's; one it doesn't set is the material's. One per filled slot of a renderer, made with its entity
/// (<see cref="World.GetShading"/>) and gone with it; what it sets holds until then and is not saved with the world.
/// Set on the main thread, read from any.
/// </summary>
public sealed class ShadingInstance(Handle<Material> material)
{
    private readonly MaterialParamDictionary _params = [];

    /// <summary>
    /// The material whose shading it instances.
    /// </summary>
    public Handle<Material> Material { get; } = material;

    public MaterialParam this[string name]
    {
        get => _params[name];
        set => _params[name] = value;
    }
}
