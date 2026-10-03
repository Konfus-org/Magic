using Magic.Extensions;
using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;
using Xunit;

namespace Magic.UnitTests.Extensions;

public sealed class SpanExtensionsTests
{
    // Two pixels side by side: one row of eight bytes.
    private static readonly byte[] Pixels = [1, 2, 3, 4, 5, 6, 7, 8];

    [Fact]
    public void Starts_with_the_png_signature()
    {
        byte[] png = Pixels.Png(2, 1);

        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A], png[..8]);
    }

    [Fact]
    public void Holds_a_header_the_data_and_an_end_chunk_in_that_order()
    {
        byte[] png = Pixels.Png(2, 1);

        Assert.Equal(["IHDR", "IDAT", "IEND"], Chunks(png).Select(chunk => chunk.Type));
    }

    [Fact]
    public void Every_chunk_carries_the_crc_of_its_type_and_data()
    {
        byte[] png = Pixels.Png(2, 1);

        Assert.All(Chunks(png), chunk => Assert.Equal(Crc32.HashToUInt32(chunk.CrcInput), chunk.Crc));
    }

    [Fact]
    public void The_header_holds_the_size_and_rgba8()
    {
        byte[] png = Pixels.Png(2, 1);

        // Width 2, height 1, then 8 bits, RGBA, deflate, no filter, no interlace.
        Assert.Equal([0, 0, 0, 2, 0, 0, 0, 1, 8, 6, 0, 0, 0], Chunks(png)[0].Data);
    }

    [Fact]
    public void The_data_inflates_to_each_row_behind_a_zero_filter_byte()
    {
        byte[] png = Pixels.Png(2, 1);

        byte[] scanlines = Inflate(Chunks(png)[1].Data);

        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7, 8], scanlines);
    }

    [Fact]
    public void Refuses_a_pixel_buffer_of_the_wrong_size()
    {
        Action encode = () => new byte[15].Png(2, 2);

        Assert.Throws<ArgumentException>(encode);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(2, 0)]
    [InlineData(-1, 2)]
    public void Refuses_a_size_that_is_not_positive(int width, int height)
    {
        Action encode = () => ReadOnlySpan<byte>.Empty.Png(width, height);

        Assert.Throws<ArgumentOutOfRangeException>(encode);
    }

    [Fact]
    public void A_text_sits_between_the_header_and_the_data()
    {
        byte[] png = Pixels.Png(2, 1, ("Magic", "state"));

        Assert.Equal(["IHDR", "iTXt", "IDAT", "IEND"], Chunks(png).Select(chunk => chunk.Type));
    }

    [Theory]
    [InlineData("state")]
    [InlineData("{\"name\":\"Zürich 東京\"}")]
    [InlineData("")]
    public void A_text_is_read_back_under_its_keyword(string text)
    {
        byte[] png = Pixels.Png(2, 1, ("Magic", text));

        string? read = png.AsSpan().PngText("Magic");

        Assert.Equal(text, read);
    }

    [Fact]
    public void Each_text_is_read_back_under_its_own_keyword()
    {
        byte[] png = Pixels.Png(2, 1, ("First", "one"), ("Second", "two"));

        string? read = png.AsSpan().PngText("Second");

        Assert.Equal("two", read);
    }

    [Fact]
    public void A_keyword_the_png_does_not_carry_reads_as_nothing()
    {
        byte[] png = Pixels.Png(2, 1, ("Magic", "state"));

        string? read = png.AsSpan().PngText("Other");

        Assert.Null(read);
    }

    [Fact]
    public void Bytes_that_are_not_a_png_carry_no_text()
    {
        string? read = Pixels.AsSpan().PngText("Magic");

        Assert.Null(read);
    }

    [Fact]
    public void A_png_cut_short_carries_no_text()
    {
        byte[] png = Pixels.Png(2, 1, ("Magic", "state"));

        string? read = png.AsSpan(0, 50).PngText("Magic");

        Assert.Null(read);
    }

    /// <summary>
    /// The chunks after the signature, each with the bytes its CRC covers.
    /// </summary>
    private static List<(string Type, byte[] Data, byte[] CrcInput, uint Crc)> Chunks(byte[] png)
    {
        List<(string, byte[], byte[], uint)> chunks = [];
        int offset = 8;

        while (offset < png.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
            string type = Encoding.ASCII.GetString(png, offset + 4, 4);
            byte[] data = png[(offset + 8)..(offset + 8 + length)];
            byte[] crcInput = png[(offset + 4)..(offset + 8 + length)];
            uint crc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + length));
            chunks.Add((type, data, crcInput, crc));
            offset += 12 + length;
        }

        return chunks;
    }

    private static byte[] Inflate(byte[] data)
    {
        using ZLibStream inflate = new(new MemoryStream(data), CompressionMode.Decompress);
        using MemoryStream raw = new();

        inflate.CopyTo(raw);

        return raw.ToArray();
    }
}
