using Magic.Utils;
using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;
using Xunit;

namespace Magic.Tests;

public sealed class PngTests
{
    [Fact]
    public void Encodes_a_well_formed_png_holding_the_pixels()
    {
        const int width = 3;
        const int height = 2;
        byte[] rgba = new byte[width * height * 4];
        for (int i = 0; i < rgba.Length; i++)
            rgba[i] = (byte)(i * 7);

        byte[] png = Png.Encode(width, height, rgba);

        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A], png[..8]);
        List<(string Type, byte[] Data)> chunks = Chunks(png);
        Assert.Equal(["IHDR", "IDAT", "IEND"], chunks.Select(c => c.Type));

        byte[] header = chunks[0].Data;
        Assert.Equal(width, BinaryPrimitives.ReadInt32BigEndian(header));
        Assert.Equal(height, BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4)));
        Assert.Equal([8, 6, 0, 0, 0], header[8..]);

        using ZLibStream inflate = new(new MemoryStream(chunks[1].Data), CompressionMode.Decompress);
        using MemoryStream raw = new();
        inflate.CopyTo(raw);
        byte[] scanlines = raw.ToArray();
        Assert.Equal(height * (1 + (width * 4)), scanlines.Length);
        for (int y = 0; y < height; y++)
        {
            int row = y * (1 + (width * 4));
            Assert.Equal(0, scanlines[row]); // filter: none
            Assert.Equal(rgba.AsSpan(y * width * 4, width * 4).ToArray(), scanlines[(row + 1)..(row + 1 + (width * 4))]);
        }
        Assert.Empty(chunks[2].Data);
    }

    [Fact]
    public void Refuses_a_pixel_buffer_of_the_wrong_size()
    {
        Assert.Throws<ArgumentException>(() => Png.Encode(2, 2, new byte[15]));
        Assert.Throws<ArgumentOutOfRangeException>(() => Png.Encode(0, 2, []));
    }

    /// <summary>Walks the chunks after the signature, checking each CRC along the way.</summary>
    private static List<(string Type, byte[] Data)> Chunks(byte[] png)
    {
        List<(string, byte[])> chunks = [];
        int offset = 8;
        while (offset < png.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
            string type = Encoding.ASCII.GetString(png, offset + 4, 4);
            byte[] data = png[(offset + 8)..(offset + 8 + length)];
            uint crc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + length));
            Assert.Equal(Crc32.HashToUInt32(png.AsSpan(offset + 4, 4 + length)), crc);
            chunks.Add((type, data));
            offset += 12 + length;
        }
        return chunks;
    }
}
