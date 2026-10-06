using Magic.Mathematics;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Magic.Contexts.Assets;

/// <summary>
/// One material or pass parameter, whatever its type in the shader: the shader's declaration decides how
/// it is read. A scalar is <see cref="X"/> (a bool is <c>X != 0</c>, an int is <c>(int)X</c>), a vector is
/// <see cref="X"/>..<see cref="W"/>, a colour is <see cref="R"/>..<see cref="A"/>, a texture is
/// <see cref="Texture"/>. Plain JSON, nothing else: <c>{ "x": 0.8 }</c>, <c>{ "x": 1, "y": 1, "z": 1, "w": 1 }</c>,
/// <c>{ "r": 1, "g": 1, "b": 1, "a": 1 }</c>, <c>{ "texture": { "id": 36 } }</c>. Missing fields are 0 or none.
/// </summary>
public struct Param
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

    [JsonIgnore]
    public readonly Vector4 Vector => new(X, Y, Z, W);

    [JsonIgnore]
    public readonly Color Color => new(R, G, B, A);

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

    public static Param Of(Color color)
    {
        return new Param { R = color.R, G = color.G, B = color.B, A = color.A };
    }

    public static Param Of(Handle<Texture> texture)
    {
        return new Param { Texture = texture };
    }

    /// <summary>
    /// A param as a preset or <c>--set</c> writes it: a number, <c>true</c>/<c>false</c>, <c>[x, y, z, w]</c> (which is also
    /// <c>[r, g, b, a]</c>, so it serves a vector or a colour), or the full object. Null for anything else.
    /// </summary>
    public static Param? Of(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                return Of(value.GetSingle());
            case JsonValueKind.True or JsonValueKind.False:
                return Of(value.GetBoolean());
            case JsonValueKind.Array:
                float[] numbers = [.. value.EnumerateArray().Take(4).Select(item => item.ValueKind == JsonValueKind.Number ? item.GetSingle() : 0f)];
                Array.Resize(ref numbers, 4);
                return new Param { X = numbers[0], Y = numbers[1], Z = numbers[2], W = numbers[3], R = numbers[0], G = numbers[1], B = numbers[2], A = numbers[3] };
            case JsonValueKind.Object:
                return value.Deserialize<Param>(AssetJson.Options);
            default:
                return null;
        }
    }
}
