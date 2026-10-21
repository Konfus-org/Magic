using Magic.Interfaces;

namespace Magic.Contexts.Assets;

/// <summary>
/// How a <see cref="Material"/> is blended, which picks the pipeline it is drawn with.
/// </summary>
public enum MaterialType : byte
{
    Opaque,

    /// <summary>
    /// Opaque, but pixels the surface makes less opaque than its alpha cutoff are not drawn.
    /// </summary>
    Masked,

    /// <summary>
    /// Blended over what is behind it, drawn after everything opaque.
    /// </summary>
    Transparent,
}

/// <summary>
/// What a surface looks like: a surface shader (<c>.surf.hlsl</c>), values for the parameters its
/// <c>MaterialParams</c> declares, and how it is blended. A <c>.mat</c> file is this class as JSON:
/// <c>{ "shader": { "id": 10001 }, "params": { "roughness": { "x": 0.8 } } }</c>.
/// </summary>
public sealed class Material : IJsonAsset<Material>
{
    public static IReadOnlyList<string> Extensions { get; } = [".mat"];

    public Handle<Material> Handle { get; init; }

    public required Handle<Shader> Shader { get; init; }

    public MaterialType Type { get; set; }

    /// <summary>
    /// Its back faces are drawn too.
    /// </summary>
    public bool DoubleSided { get; set; }

    /// <summary>
    /// Values by the names of the shader's <c>MaterialParams</c> members, matched as HLSL does, case and all; one left
    /// out takes the shader's default.
    /// </summary>
    public IReadOnlyDictionary<string, MaterialParam> Params { get; set; }
        = new Dictionary<string, MaterialParam>(StringComparer.Ordinal);
}

/// <summary>
/// One material parameter, whatever its type in the shader, whose declaration says which part is read: a scalar is
/// <see cref="X"/> (a bool is non-zero), a vector <see cref="X"/> to <see cref="W"/>, a colour <see cref="R"/> to
/// <see cref="A"/>, a texture <see cref="Texture"/> or <see cref="RenderTexture"/>, a curve <see cref="Curve"/>. In a
/// file: <c>{ "x": 0.8 }</c>, <c>{ "r": 1, "g": 0.5, "b": 0, "a": 1 }</c>, <c>{ "texture": { "id": 36 } }</c>,
/// <c>{ "curve": { "id": 3364 } }</c>; what it leaves out is 0 or none, and a curve left out the default curve.
/// </summary>
public sealed class MaterialParam
{
    public float X { get; set; }

    public float Y { get; set; }

    public float Z { get; set; }

    public float W { get; set; }

    public float R { get; set; }

    public float G { get; set; }

    public float B { get; set; }

    public float A { get; set; }

    public Handle<Texture> Texture { get; set; }

    /// <summary>
    /// A texture a camera draws into, sampled in place of <see cref="Texture"/>.
    /// </summary>
    public Handle<RenderTexture> RenderTexture { get; set; }

    public Handle<Curve> Curve { get; set; }
}
