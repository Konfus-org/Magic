using System.Diagnostics.CodeAnalysis;
using System.Numerics;
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
/// <c>{ "shader": { "id": 10001 }, "params": { "roughness": { "x": 0.8 } } }</c>. Its params, how it is blended and
/// its sides may be set while it is drawn, on the main thread, and are drawn so from the next frame; what is set holds
/// until the material is made again, as its file changes, and is not saved with the world.
/// </summary>
public sealed class Material : IJsonAsset<Material>
{
    public static IReadOnlyList<string> Extensions { get; } = [".mat"];

    public Handle<Material> Handle { get; init; }

    public required Handle<Shader> Shader { get; init; }

    /// <summary>
    /// How it is blended.
    /// </summary>
    public MaterialType Type { get; set; }

    /// <summary>
    /// Its back faces are drawn too.
    /// </summary>
    public bool IsDoubleSided { get; set; }

    /// <summary>
    /// Values by the names of the shader's <c>MaterialParams</c> members, matched as HLSL does, case and all; one left
    /// out takes the shader's default. Its values change, never the dictionary, so whatever draws it sees each change.
    /// </summary>
    public MaterialParamDictionary Params { get; init; } = [];
}

/// <summary>
/// One material parameter, whatever its type in the shader, whose declaration says which part is read: a scalar is
/// <see cref="X"/> (a bool is non-zero), a vector <see cref="X"/> to <see cref="W"/>, a colour <see cref="R"/> to
/// <see cref="A"/>, a texture <see cref="Texture"/> or <see cref="RenderTexture"/>, a curve <see cref="Curve"/>. In a
/// file: <c>{ "x": 0.8 }</c>, <c>{ "r": 1, "g": 0.5, "b": 0, "a": 1 }</c>, <c>{ "texture": { "id": 36 } }</c>,
/// <c>{ "curve": { "id": 3364 } }</c>; what it leaves out is 0 or none, and a curve left out the default curve. A
/// value, never changed once made: a param changes by setting another in its place, which its dictionary counts. It
/// converts from a number, a vector, a colour or a texture's handle, so
/// <c>material.Params["tint"] = new Color(1, 0, 0)</c> sets one.
/// </summary>
[SuppressMessage(
    "Usage",
    "CA2225:Operator overloads have named alternates",
    Justification = "Gems and scripts are C#, where the conversion is how a param is written; a FromSingle beside "
        + "each would be a second way to write the same thing.")]
public sealed record MaterialParam
{
    public float X { get; init; }

    public float Y { get; init; }

    public float Z { get; init; }

    public float W { get; init; }

    public float R { get; init; }

    public float G { get; init; }

    public float B { get; init; }

    public float A { get; init; }

    public Handle<Texture> Texture { get; init; }

    /// <summary>
    /// A texture a camera draws into, sampled in place of <see cref="Texture"/>.
    /// </summary>
    public Handle<RenderTexture> RenderTexture { get; init; }

    public Handle<Curve> Curve { get; init; }

    /// <summary>
    /// A scalar, or a bool as 0 or 1.
    /// </summary>
    public static implicit operator MaterialParam(float x) => new() { X = x };

    public static implicit operator MaterialParam(Vector2 value) => new() { X = value.X, Y = value.Y };

    public static implicit operator MaterialParam(Vector3 value) => new() { X = value.X, Y = value.Y, Z = value.Z };

    public static implicit operator MaterialParam(Vector4 value)
        => new() { X = value.X, Y = value.Y, Z = value.Z, W = value.W };

    public static implicit operator MaterialParam(Color color)
        => new() { R = color.R, G = color.G, B = color.B, A = color.A };

    public static implicit operator MaterialParam(Handle<Texture> texture) => new() { Texture = texture };
}
