using System.Numerics;

namespace Magic.GpuWorld;

/// <summary>
/// Values packed as <c>Core/Pack.slang</c> unpacks them: the CPU side of the same bits.
/// </summary>
internal static class Packing
{
    /// <summary>
    /// A unit vector as its octahedral point at 16 bits an axis (Cigolle et al., "A Survey of Efficient
    /// Representations for Independent Unit Vectors", 2014).
    /// </summary>
    public static uint EncodeUnitVector(Vector3 unit) =>
        EncodeUnorm2x16((EncodeOctahedral(unit) * 0.5f) + new Vector2(0.5f));

    /// <summary>
    /// The unit vector <see cref="EncodeUnitVector"/> packed.
    /// </summary>
    public static Vector3 DecodeUnitVector(uint packed) =>
        DecodeOctahedral((new Vector2(packed & 0xFFFF, packed >> 16) / 65535f * 2) - Vector2.One);

    /// <summary>
    /// A unit vector as a point in [-1, 1]², the octahedron folded flat; the centre for no direction.
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
    /// The unit vector at a point of the folded octahedron, as <c>Core/Pack.slang</c>'s DecodeOctahedral unfolds it.
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
    /// A number in [-1, 1] as a signed 16-bit fraction in the low half of a word.
    /// </summary>
    public static uint EncodeSnorm16(float v) => (ushort)(short)MathF.Round(Math.Clamp(v, -1, 1) * 32767);
}
