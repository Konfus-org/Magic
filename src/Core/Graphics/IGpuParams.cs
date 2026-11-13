using System.Diagnostics.CodeAnalysis;
using Magic.Assets;

namespace Magic.Graphics;

/// <summary>
/// The params that <see cref="GpuPacker.Pack{TParams}"/> packs into a row, by the names of a shader struct's fields.
/// </summary>
/// <remarks>
/// Implement it on a struct, so that a call through the constraint is not boxed.
/// </remarks>
public interface IGpuParams
{
    /// <summary>
    /// Gets the param that sets the field <paramref name="name"/>, or false when none sets it.
    /// </summary>
    bool TryGetParam(string name, [MaybeNullWhen(false)] out MaterialParam param);

    /// <summary>
    /// Gets whether the texture that <paramref name="param"/> samples is on the GPU, and its heap ids and its sampler's
    /// when it is.
    /// </summary>
    GpuState GetTexture(MaterialParam param, out uint texture, out uint sampler);
}
