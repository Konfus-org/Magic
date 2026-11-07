using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;
using Magic.Assets;

namespace Magic.Ecs.Components;

/// <summary>
/// The params a slot (<see cref="MaterialInstance"/>) sets in place of its material's, by the names of the shader's
/// fields, <c>renderer.Materials[0].Overrides["color"] = new Color(1, 0, 0)</c>, which whatever draws it reads over the
/// material's; one it doesn't set is the material's. Plain data inline in the component, so a change is drawn from the
/// next frame and saved with the world as the component differs from its prefab's: up to
/// <see cref="Capacity"/> params, each named in up to <see cref="MaterialOverride.NameBytes"/> ASCII characters, a
/// scalar, vector, colour or texture (a render texture or a curve is the material's alone). In a file, an object of
/// params by name, <c>{ "color": { "r": 1, "a": 1 } }</c>.
/// </summary>
[JsonConverter(typeof(MaterialOverridesJsonConverter))]
public struct MaterialOverrides : IEquatable<MaterialOverrides>
{
    /// <summary>
    /// How many params one sets at most: a tint and a texture. // PROTO: every slot of every renderer carries them,
    /// so more waits for a slot that needs them.
    /// </summary>
    public const int Capacity = 2;

    private MaterialOverrideSlots _set;

    /// <summary>
    /// The params it sets, by name.
    /// </summary>
    public readonly ImmutableArray<KeyValuePair<string, MaterialParam>> Params
    {
        get
        {
            ImmutableArray<KeyValuePair<string, MaterialParam>>.Builder set = ImmutableArray.CreateBuilder<
                KeyValuePair<string, MaterialParam>
            >();
            foreach (MaterialOverride param in _set)
            {
                if (param.IsSet)
                    set.Add(new KeyValuePair<string, MaterialParam>(param.Name, param.Param));
            }

            return set.ToImmutable();
        }
    }

    /// <summary>
    /// The param it sets by that name; reading one it doesn't set throws. Setting one replaces what it set under that
    /// name, or takes a free place, and throws when every place is taken or the name is not ASCII of up to
    /// <see cref="MaterialOverride.NameBytes"/> characters.
    /// </summary>
    public MaterialParam this[string name]
    {
        readonly get =>
            TryGet(name, out MaterialParam? param)
                ? param
                : throw new KeyNotFoundException($"The slot sets no material param named {name}.");
        set => Set(name, value);
    }

    public static bool operator ==(MaterialOverrides left, MaterialOverrides right) => left.Equals(right);

    public static bool operator !=(MaterialOverrides left, MaterialOverrides right) => !left.Equals(right);

    /// <summary>
    /// The param it sets by that name, false for one it doesn't.
    /// </summary>
    public readonly bool TryGet(string name, [MaybeNullWhen(false)] out MaterialParam param)
    {
        Span<byte> encoded = stackalloc byte[MaterialOverride.NameBytes];
        if (MaterialOverride.TryEncodeName(name, encoded))
        {
            foreach (MaterialOverride candidate in _set)
            {
                if (candidate.IsNamed(encoded))
                {
                    param = candidate.Param;

                    return true;
                }
            }
        }

        param = null;

        return false;
    }

    /// <summary>
    /// Whether both set the same params: the same bytes, as the instance table compares renderers.
    /// </summary>
    public readonly bool Equals(MaterialOverrides other) => AsBytes(in this).SequenceEqual(AsBytes(in other));

    public override readonly bool Equals(object? obj) => obj is MaterialOverrides other && Equals(other);

    public override readonly int GetHashCode()
    {
        HashCode hash = default;
        hash.AddBytes(AsBytes(in this));

        return hash.ToHashCode();
    }

    private static ReadOnlySpan<byte> AsBytes(ref readonly MaterialOverrides overrides) =>
        MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in overrides, 1));

    private void Set(string name, MaterialParam param)
    {
        Span<byte> encoded = stackalloc byte[MaterialOverride.NameBytes];
        if (!MaterialOverride.TryEncodeName(name, encoded))
        {
            throw new ArgumentException(
                $"A material param's name is 1 to {MaterialOverride.NameBytes} ASCII characters, not '{name}'.",
                nameof(name)
            );
        }

        int free = -1;
        for (int i = 0; i < Capacity; i++)
        {
            if (_set[i].IsNamed(encoded))
            {
                _set[i] = MaterialOverride.Create(encoded, param);

                return;
            }

            if (free < 0 && !_set[i].IsSet)
                free = i;
        }

        if (free < 0)
            throw new InvalidOperationException($"{name} was not set: a slot sets at most {Capacity} params.");

        _set[free] = MaterialOverride.Create(encoded, param);
    }
}

/// <summary>
/// One param a <see cref="MaterialOverrides"/> sets: the scalar, vector, colour or texture a
/// <see cref="MaterialParam"/> holds, and its name as ASCII bytes, zero-padded, none in a free place.
/// </summary>
internal struct MaterialOverride
{
    public const int NameBytes = 16;

    public float X;

    public float Y;

    public float Z;

    public float W;

    public float R;

    public float G;

    public float B;

    public float A;

    public Handle<Texture> Texture;

    private MaterialOverrideName _name;

    public readonly bool IsSet => _name[0] != 0;

    public readonly string Name => Encoding.ASCII.GetString(((ReadOnlySpan<byte>)_name).TrimEnd((byte)0));

    public readonly MaterialParam Param =>
        new()
        {
            X = X,
            Y = Y,
            Z = Z,
            W = W,
            R = R,
            G = G,
            B = B,
            A = A,
            Texture = Texture,
        };

    /// <summary>
    /// A set place named <paramref name="name"/>, as <see cref="TryEncodeName"/> encodes it, holding
    /// <paramref name="param"/>.
    /// </summary>
    public static MaterialOverride Create(ReadOnlySpan<byte> name, MaterialParam param)
    {
        MaterialOverride made = new()
        {
            X = param.X,
            Y = param.Y,
            Z = param.Z,
            W = param.W,
            R = param.R,
            G = param.G,
            B = param.B,
            A = param.A,
            Texture = param.Texture,
        };
        name.CopyTo(made._name);

        return made;
    }

    /// <summary>
    /// Writes <paramref name="name"/> into <paramref name="encoded"/>'s <see cref="NameBytes"/> bytes, zero-padded;
    /// false when it is empty, longer or not ASCII.
    /// </summary>
    public static bool TryEncodeName(string name, Span<byte> encoded)
    {
        if (name.Length == 0 || name.Length > NameBytes || !Ascii.IsValid(name))
            return false;

        encoded.Clear();
        Encoding.ASCII.GetBytes(name, encoded);

        return true;
    }

    public readonly bool IsNamed(ReadOnlySpan<byte> encoded) => ((ReadOnlySpan<byte>)_name).SequenceEqual(encoded);
}

[InlineArray(MaterialOverride.NameBytes)]
internal struct MaterialOverrideName
{
    private byte _first;
}

[InlineArray(MaterialOverrides.Capacity)]
internal struct MaterialOverrideSlots
{
    private MaterialOverride _first;
}
