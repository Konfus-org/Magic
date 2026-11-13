using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Magic.Assets;

namespace Magic.Graphics;

/// <summary>
/// Packs values into the words that shaders read, as <c>Core/Pack.slang</c> unpacks them, packs a shader struct's row
/// from its params, and reads values back from a cache's bytes.
/// </summary>
/// <remarks>
/// It holds no state, and allocates only in <see cref="Read{T}"/> and <see cref="FindUnnamed"/>.
/// </remarks>
public static class GpuPacker
{
    /// <summary>
    /// Packs the params of <paramref name="source"/> into <paramref name="words"/>, each into the field of
    /// <paramref name="fields"/> that it names.
    /// </summary>
    /// <remarks>
    /// A texture's heap id goes into its <c>TextureRef</c> field, and its sampler's into the <c>SamplerRef</c> field
    /// named for it with "Sampler" after. A texture given to a field that is no texture, or numbers to one that is, fits
    /// nothing and leaves the field as it was. A field that no param sets stays as it was.
    /// </remarks>
    /// <returns>
    /// Loading while a texture loads, the first texture that failed, which packs as none, and the first field that no
    /// param fits.
    /// </returns>
    public static GpuPacked Pack<TParams>(ImmutableArray<ShaderField> fields, TParams source, Span<uint> words)
        where TParams : struct, IGpuParams
    {
        GpuState state = GpuState.Ready;
        ulong failedTexture = 0;
        string? misfit = null;
        foreach (ShaderField field in fields)
        {
            if (!source.TryGetParam(field.Name, out MaterialParam? param))
                continue;

            bool isTexture = field.Struct == "TextureRef";
            bool isColor = param.R != 0 || param.G != 0 || param.B != 0 || param.A != 0;
            bool hasNumbers = param.X != 0 || param.Y != 0 || param.Z != 0 || param.W != 0 || isColor;
            ulong texture =
                param.RenderTexture != Handle<RenderTexture>.None ? param.RenderTexture.Id : param.Texture.Id;
            if (isTexture ? texture == 0 && hasNumbers : texture != 0)
            {
                misfit ??= field.Name;

                continue;
            }

            if (isTexture)
            {
                if (texture == 0)
                    continue;

                GpuState got = source.GetTexture(param, out uint id, out uint sampler);
                PackTexture(fields, field.Name, id, sampler, words);
                if (got == GpuState.Failed && failedTexture == 0)
                    failedTexture = texture;
                else if (got == GpuState.Loading)
                    state = GpuState.Loading;

                continue;
            }

            // PROTO: a param that sets a colour fills the field from it, and one that sets none from its vector.
            Span<uint> into = words.Slice((int)field.Offset / 4, (int)field.Size / 4);
            ReadOnlySpan<float> values = isColor
                ? [param.R, param.G, param.B, param.A]
                : [param.X, param.Y, param.Z, param.W];
            for (int i = 0; i < into.Length && i < values.Length; i++)
            {
                into[i] = BitConverter.SingleToUInt32Bits(values[i]);
            }
        }

        return new GpuPacked(state, failedTexture, misfit);
    }

    /// <summary>
    /// Finds the first of <paramref name="names"/> that names no field of <paramref name="fields"/>, null for none.
    /// </summary>
    /// <remarks>
    /// It allocates: keep its result until an asset is read again.
    /// </remarks>
    public static string? FindUnnamed(ImmutableArray<ShaderField> fields, IEnumerable<string> names) =>
        names.FirstOrDefault(name => !fields.Any(field => field.Name == name));

    /// <summary>
    /// Reads the first <paramref name="count"/> values of <paramref name="bytes"/>, in native byte order, and moves
    /// <paramref name="bytes"/> past them.
    /// </summary>
    public static ImmutableArray<T> Read<T>(ref ReadOnlySpan<byte> bytes, int count)
        where T : struct
    {
        int length = count * Unsafe.SizeOf<T>();
        ImmutableArray<T> items = [.. MemoryMarshal.Cast<byte, T>(bytes[..length])];
        bytes = bytes[length..];

        return items;
    }

    /// <summary>
    /// Encodes a unit vector as its octahedral point at 16 bits an axis (Cigolle et al., "A Survey of Efficient
    /// Representations for Independent Unit Vectors", 2014).
    /// </summary>
    public static uint EncodeUnitVector(Vector3 unit) =>
        EncodeUnorm2x16((EncodeOctahedral(unit) * 0.5f) + new Vector2(0.5f));

    /// <summary>
    /// Decodes the unit vector <see cref="EncodeUnitVector"/> packed.
    /// </summary>
    public static Vector3 DecodeUnitVector(uint packed) =>
        DecodeOctahedral((new Vector2(packed & 0xFFFF, packed >> 16) / 65535f * 2) - Vector2.One);

    /// <summary>
    /// Encodes a unit vector as a point in [-1, 1]², the octahedron folded flat; the centre for no direction.
    /// </summary>
    public static Vector2 EncodeOctahedral(Vector3 unit)
    {
        float sum = Math.Abs(unit.X) + Math.Abs(unit.Y) + Math.Abs(unit.Z);
        if (sum < 1e-12f)
            return Vector2.Zero;

        Vector3 n = unit / sum;
        if (n.Z >= 0)
            return new Vector2(n.X, n.Y);

        return new Vector2((1 - Math.Abs(n.Y)) * (n.X >= 0 ? 1 : -1), (1 - Math.Abs(n.X)) * (n.Y >= 0 ? 1 : -1));
    }

    /// <summary>
    /// Decodes the unit vector at a point of the folded octahedron, as <c>Core/Pack.slang</c>'s DecodeOctahedral does.
    /// </summary>
    public static Vector3 DecodeOctahedral(Vector2 flat)
    {
        Vector3 n = new(flat.X, flat.Y, 1 - Math.Abs(flat.X) - Math.Abs(flat.Y));
        float t = Math.Clamp(-n.Z, 0, 1);
        n.X += n.X >= 0 ? -t : t;
        n.Y += n.Y >= 0 ? -t : t;

        return Vector3.Normalize(n);
    }

    public static uint EncodeUnorm2x16(Vector2 v)
    {
        uint x = (uint)MathF.Round(Math.Clamp(v.X, 0, 1) * 65535);
        uint y = (uint)MathF.Round(Math.Clamp(v.Y, 0, 1) * 65535);

        return x | (y << 16);
    }

    /// <summary>
    /// Encodes a number in [-1, 1] as a signed 16-bit fraction in the low half of a word.
    /// </summary>
    public static uint EncodeSnorm16(float v) => (ushort)(short)MathF.Round(Math.Clamp(v, -1, 1) * 32767);

    /// <summary>
    /// Writes a texture's heap id into the field <paramref name="name"/> of <paramref name="words"/>, and its sampler's
    /// into the <c>SamplerRef</c> field named for it with "Sampler" after.
    /// </summary>
    private static void PackTexture(
        ImmutableArray<ShaderField> fields,
        string name,
        uint texture,
        uint sampler,
        Span<uint> words
    )
    {
        foreach (ShaderField field in fields)
        {
            if (field.Name == name)
                words[(int)field.Offset / 4] = texture;
            else if (field.Struct == "SamplerRef" && IsSamplerOf(field.Name, name))
                words[(int)field.Offset / 4] = sampler;
        }

        static bool IsSamplerOf(string field, string texture) =>
            field.Length == texture.Length + "Sampler".Length
            && field.StartsWith(texture, StringComparison.Ordinal)
            && field.EndsWith("Sampler", StringComparison.Ordinal);
    }
}

/// <summary>
/// What <see cref="GpuPacker.Pack{TParams}"/> packed: whether its textures are on the GPU, the id of the first texture
/// that failed, 0 for none, and the first field that no param fits, null for none.
/// </summary>
public readonly record struct GpuPacked(GpuState State, ulong FailedTexture, string? Misfit);
