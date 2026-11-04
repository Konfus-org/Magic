using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.IO.Hashing;

namespace Magic.Images;

/// <summary>
/// Writes 8-bit RGBA pixels as a PNG: one image chunk of every row unfiltered and deflated. Enough for a screenshot,
/// which nothing reads back.
/// </summary>
public static class Png
{
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// <paramref name="rgba"/>, <paramref name="width"/> by <paramref name="height"/> texels of four bytes each, rows
    /// top first, as a PNG file's bytes.
    /// </summary>
    public static byte[] Encode(int width, int height, ReadOnlySpan<byte> rgba)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (rgba.Length != width * height * 4)
            throw new ArgumentException($"{rgba.Length} bytes are not {width} by {height} RGBA texels.", nameof(rgba));

        // Each row is prefixed with filter type 0, none.
        int rowBytes = width * 4;
        byte[] raw = new byte[(rowBytes + 1) * height];
        for (int y = 0; y < height; y++)
        {
            rgba.Slice(y * rowBytes, rowBytes).CopyTo(raw.AsSpan((y * (rowBytes + 1)) + 1));
        }

        using MemoryStream deflated = new();
        using (ZLibStream zlib = new(deflated, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8; // bits per channel
        header[9] = 6; // colour type: RGBA
        header[10] = 0; // deflate
        header[11] = 0; // filter method
        header[12] = 0; // no interlace

        using MemoryStream file = new();
        file.Write(Signature);
        WriteChunk(file, "IHDR"u8, header);
        WriteChunk(file, "IDAT"u8, deflated.GetBuffer().AsSpan(0, (int)deflated.Length));
        WriteChunk(file, "IEND"u8, []);

        return file.ToArray();
    }

    private static void WriteChunk(Stream file, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        file.Write(length);
        file.Write(type);
        file.Write(data);
        Crc32 crc = new();
        crc.Append(type);
        crc.Append(data);
        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, crc.GetCurrentHashAsUInt32());
        file.Write(checksum);
    }
}
