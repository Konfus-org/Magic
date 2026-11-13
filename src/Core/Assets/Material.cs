using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Magic.Assets;

/// <summary>
/// How much of what is behind a <see cref="Material"/> shows through it, which picks the pipeline it is drawn with.
/// </summary>
public enum Transparency : byte
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
/// What a surface looks like: a material shader, values for the parameters of its struct, and how much shows through.
/// </summary>
/// <remarks>
/// The shader is a module that implements the material contract of the shader library. A <c>.mat</c> file is this class
/// as JSON: <c>{ "shader": { "id": 10001 }, "params": { "roughness": { "x": 0.8 } } }</c>. Set the params, the
/// transparency and the sides only on the game thread; you can set them while it is drawn. A change shows from the next
/// frame and holds until the material is made again when its file changes. The world does not save it.
/// </remarks>
[AssetFormat(AssetFileFormat.Json)]
public sealed class Material : IAsset<Material>
{
    public required Handle<Shader> Shader { get; init; }

    /// <summary>
    /// How much of what is behind it shows through.
    /// </summary>
    public Transparency Transparency { get; set; }

    /// <summary>
    /// Its back faces are drawn too.
    /// </summary>
    public bool IsDoubleSided { get; set; }

    /// <summary>
    /// Values by the case-sensitive names of the fields of the shader's struct; a field left out takes its initializer.
    /// </summary>
    /// <remarks>
    /// Its values change, never the dictionary, so the renderer sees each change.
    /// </remarks>
    public MaterialParamDictionary Params { get; init; } = [];
}

/// <summary>
/// One material parameter of any shader type; the declaration in the shader says which part is read.
/// </summary>
/// <remarks>
/// A scalar reads <see cref="X"/> (a bool is non-zero), a vector <see cref="X"/> to <see cref="W"/>, a colour
/// <see cref="R"/> to <see cref="A"/>, a texture <see cref="Texture"/> or <see cref="RenderTexture"/>, and a curve
/// <see cref="Curve"/>. In a file: <c>{ "x": 0.8 }</c>, <c>{ "r": 1, "g": 0.5, "b": 0, "a": 1 }</c>,
/// <c>{ "texture": { "id": 36 } }</c>, <c>{ "curve": { "id": 3364 } }</c>. A part left out is 0 or none, and a curve
/// left out is the default curve. It is a value that never changes: to change a param, set another in its place, and
/// the dictionary counts the change. It converts from a number, a vector, a colour or a texture's handle, so
/// <c>material.Params["tint"] = new Color(1, 0, 0)</c> sets one.
/// </remarks>
[SuppressMessage(
    "Usage",
    "CA2225:Operator overloads have named alternates",
    Justification = "Gems and scripts are C#, where the conversion is how a param is written; a FromSingle beside "
        + "each would be a second way to write the same thing."
)]
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

    public static implicit operator MaterialParam(Vector3 value) =>
        new()
        {
            X = value.X,
            Y = value.Y,
            Z = value.Z,
        };

    public static implicit operator MaterialParam(Vector4 value) =>
        new()
        {
            X = value.X,
            Y = value.Y,
            Z = value.Z,
            W = value.W,
        };

    public static implicit operator MaterialParam(Color color) =>
        new()
        {
            R = color.R,
            G = color.G,
            B = color.B,
            A = color.A,
        };

    public static implicit operator MaterialParam(Handle<Texture> texture) => new() { Texture = texture };
}
