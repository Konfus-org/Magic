using System.Numerics;
using System.Text.Json.Serialization;

namespace Magic.Contexts.Assets;

/// <summary>
/// One material or pass parameter, whatever its type in the shader: the shader's declaration decides how
/// it is read. A scalar is <see cref="X"/> (a bool is <c>X != 0</c>, an int is <c>(int)X</c>), a vector is
/// <see cref="X"/>..<see cref="W"/>, a texture is <see cref="Texture"/>. Plain JSON, nothing else:
/// <c>{ "x": 0.8 }</c>, <c>{ "x": 1, "y": 1, "z": 1, "w": 1 }</c>, <c>{ "texture": { "id": 36 } }</c>.
/// Missing fields are 0 or none.
/// </summary>
public struct Param
{
    public float X { get; set; }

    public float Y { get; set; }

    public float Z { get; set; }

    public float W { get; set; }

    public Handle<Texture> Texture { get; set; }

    [JsonIgnore]
    public readonly Vector4 Vector => new(X, Y, Z, W);

    public static Param Of(float x)
    {
        return new Param { X = x };
    }

    public static Param Of(bool value)
    {
        return new Param { X = value ? 1f : 0f };
    }

    public static Param Of(Vector2 v)
    {
        return new Param { X = v.X, Y = v.Y };
    }

    public static Param Of(Vector3 v)
    {
        return new Param { X = v.X, Y = v.Y, Z = v.Z };
    }

    public static Param Of(Vector4 v)
    {
        return new Param { X = v.X, Y = v.Y, Z = v.Z, W = v.W };
    }

    public static Param Of(Handle<Texture> texture)
    {
        return new Param { Texture = texture };
    }
}
