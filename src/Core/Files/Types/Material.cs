using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Magic.Files.Types;

/// <summary>
/// What a surface is made of, in one place: how it looks (<see cref="Visuals"/>), how a body grips and bounces off it
/// (<see cref="Physics"/>), and how it sounds (<see cref="Acoustics"/>). Whatever draws, simulates or plays the world
/// reads its own section from the materials of an entity's renderer. A <c>.mat</c> file is this class as JSON:
/// <c>{ "visuals": { "shader": { "id": 10001 } }, "physics": { "grip": "slippery" }, "acoustics": "carpet" }</c>.
/// </summary>
[AssetFormat(AssetFileFormat.Json)]
public sealed class Material : IAsset<Material>
{
    public required MaterialVisuals Visuals { get; init; }

    public MaterialPhysics Physics { get; init; } = new();

    /// <summary>
    /// How much of a sound it soaks up, how it scatters the rest and how much passes through it.
    /// </summary>
    public AcousticMaterial Acoustics { get; set; }
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
