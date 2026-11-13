using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Magic.Assets;
using Magic.Ecs.Components;

namespace Magic.Graphics;

/// <summary>
/// Gets whether the texture that <paramref name="param"/> samples is on the GPU, and its heap ids and its sampler's
/// when it is; the first ask starts reading it.
/// </summary>
public delegate GpuState TextureLookup(MaterialParam param, out uint texture, out uint sampler);

/// <summary>
/// The params of a slot over those of its material, with their textures got through <paramref name="Textures"/>.
/// </summary>
public readonly record struct MaterialParams(Material Material, MaterialOverrides Overrides, TextureLookup Textures)
    : IGpuParams
{
    public bool TryGetParam(string name, [MaybeNullWhen(false)] out MaterialParam param) =>
        Overrides.TryGet(name, out param) || Material.Params.TryGetValue(name, out param);

    public GpuState GetTexture(MaterialParam param, out uint texture, out uint sampler) =>
        Textures(param, out texture, out sampler);

    /// <summary>
    /// Finds the first param of the material or of the slot that names no field of <paramref name="fields"/>, null for
    /// none.
    /// </summary>
    /// <remarks>
    /// It allocates: keep its result until an asset is read again.
    /// </remarks>
    public string? FindUnusedParam(ImmutableArray<ShaderField> fields) =>
        Material
            .Params.Keys.Concat(Overrides.Params.Select(param => param.Key))
            .FirstOrDefault(name => !fields.Any(field => field.Name == name));
}
