using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;

namespace Magic.Utils;

/// <summary>
/// Writes 8-bit RGBA pixels as a PNG: one IHDR, one IDAT holding every unfiltered scanline deflated, one IEND.
/// Enough for screenshots, which is all the host writes; nothing is read back (that is a loader gem's job).
/// </summary>
public static class Png
{
    private static ReadOnlySpan<byte> Signature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary><paramref name="rgba"/> is <paramref name="width"/> * <paramref name="height"/> * 4 bytes, rows tightly packed top to bottom.</summary>
    public static byte[] Encode(int width, int height, ReadOnlySpan<byte> rgba)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        int rowBytes = checked(width * 4);
        if (rgba.Length != checked(rowBytes * height))
            throw new ArgumentException($"Expected {rowBytes * height} bytes for {width}x{height} RGBA, got {rgba.Length}.", nameof(rgba));

        // Every scanline is preceded by its filter type; 0 leaves the bytes as they are.
        byte[] raw = new byte[height * (1 + rowBytes)];
        for (int y = 0; y < height; y++)
            rgba.Slice(y * rowBytes, rowBytes).CopyTo(raw.AsSpan((y * (1 + rowBytes)) + 1));

        using MemoryStream compressed = new();
        using (ZLibStream zlib = new(compressed, CompressionLevel.Fastest, leaveOpen: true))
            zlib.Write(raw);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8; // bit depth
        header[9] = 6; // colour type: RGBA
        header[10] = 0; // compression: deflate
        header[11] = 0; // filter method: adaptive (per-scanline type bytes, all 0 above)
        header[12] = 0; // interlace: none

        using MemoryStream png = new();
        png.Write(Signature);
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    /// <summary>Length, type, data, then a CRC over type and data; all integers big-endian.</summary>
    private static void Chunk(Stream png, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> typeBytes = stackalloc byte[4];
        Encoding.ASCII.GetBytes(type, typeBytes);

        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        png.Write(number);
        png.Write(typeBytes);
        png.Write(data);

        Crc32 crc = new();
        crc.Append(typeBytes);
        crc.Append(data);
        BinaryPrimitives.WriteUInt32BigEndian(number, crc.GetCurrentHashAsUInt32());
        png.Write(number);
    }
}
