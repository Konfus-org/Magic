using System.Numerics;

namespace Magic.GIRendering;

/// <summary>
/// Values packed as <c>Core/Pack.slang</c> unpacks them: the CPU side of the same bits.
/// </summary>
internal static class Packing
{
    /// <summary>
    /// A unit vector as its octahedral point at 16 bits an axis (Cigolle et al., "A Survey of Efficient
    /// Representations for Independent Unit Vectors", 2014).
    /// </summary>
    public static uint EncodeUnitVector(Vector3 unit)
    {
        float sum = Math.Abs(unit.X) + Math.Abs(unit.Y) + Math.Abs(unit.Z);
        if (sum < 1e-12f)
            return EncodeUnorm2x16(new Vector2(0.5f, 0.5f));

        Vector3 n = unit / sum;
        Vector2 flat = new(n.X, n.Y);
        if (n.Z < 0)
            flat = new Vector2((1 - Math.Abs(n.Y)) * (n.X >= 0 ? 1 : -1), (1 - Math.Abs(n.X)) * (n.Y >= 0 ? 1 : -1));

        return EncodeUnorm2x16((flat * 0.5f) + new Vector2(0.5f));
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
