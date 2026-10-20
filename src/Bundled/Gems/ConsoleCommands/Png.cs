using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.IO.Hashing;
using Magic.Contexts.Gpu;

namespace Magic.ConsoleCommands;

/// <summary>
/// An image as a PNG file: 8-bit RGBA, each row unfiltered, all of them deflated into one chunk. The smallest PNG that
/// every viewer reads, which is all a screenshot needs.
/// </summary>
internal static class Png
{
    internal static byte[] Encode(GpuImage image)
    {
        using MemoryStream png = new();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);

        // Width, height, 8 bits a channel, RGBA, then deflate, no filtering scheme and no interlacing.
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, image.Width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], image.Height);
        header[8] = 8;
        header[9] = 6;
        Chunk(png, "IHDR"u8, header);

        using MemoryStream rows = new();
        using (ZLibStream deflate = new(rows, CompressionLevel.Fastest, leaveOpen: true))
        {
            int stride = image.Width * 4;
            ReadOnlySpan<byte> pixels = image.Pixels.Span;
            for (int row = 0; row < image.Height; row++)
            {
                deflate.WriteByte(0);
                deflate.Write(pixels.Slice(row * stride, stride));
            }
        }

        Chunk(png, "IDAT"u8, rows.GetBuffer().AsSpan(0, (int)rows.Length));
        Chunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    /// <summary>
    /// A chunk: its length, its type, its data, and the CRC of its type and data.
    /// </summary>
    private static void Chunk(MemoryStream png, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(word, data.Length);
        png.Write(word);
        png.Write(type);
        png.Write(data);
        Crc32 crc = new();
        crc.Append(type);
        crc.Append(data);
        BinaryPrimitives.WriteUInt32BigEndian(word, crc.GetCurrentHashAsUInt32());
        png.Write(word);
    }
}
