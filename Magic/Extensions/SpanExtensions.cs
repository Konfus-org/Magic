using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;

namespace Magic.Extensions;

internal static class SpanExtensions
{
    private static ReadOnlySpan<byte> PngSignature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    extension(ReadOnlySpan<byte> span)
    {
        /// <summary>
        /// The span's 8-bit RGBA pixels (<paramref name="width"/> * <paramref name="height"/> * 4 bytes, rows tightly
        /// packed top to bottom) as a PNG: one IHDR, an iTXt for each of <paramref name="texts"/>, one IDAT holding
        /// every unfiltered scanline deflated, one IEND. Enough for screenshots, which is all the host writes; of
        /// it only the texts are read back, by <see cref="PngText"/> (the pixels are a loader gem's job).
        /// A keyword is 1 to 79 Latin-1 characters; the text is anything.
        /// </summary>
        public byte[] Png(int width, int height, params ReadOnlySpan<(string Keyword, string Text)> texts)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

            int rowBytes = checked(width * 4);
            if (span.Length != checked(rowBytes * height))
                throw new ArgumentException($"Expected {rowBytes * height} bytes for {width}x{height} RGBA, got {span.Length}.", nameof(span));

            // Every scanline is preceded by its filter type; 0 leaves the bytes as they are.
            byte[] raw = new byte[height * (1 + rowBytes)];
            for (int y = 0; y < height; y++)
                span.Slice(y * rowBytes, rowBytes).CopyTo(raw.AsSpan((y * (1 + rowBytes)) + 1));

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
            png.Write(PngSignature);
            WritePngChunk(png, "IHDR", header);
            foreach ((string keyword, string text) in texts)
            {
                // Keyword, its terminator, uncompressed (flag and method), no language, no translated keyword, then UTF-8.
                WritePngChunk(png, "iTXt", [.. Encoding.Latin1.GetBytes(keyword), 0, 0, 0, 0, 0, .. Encoding.UTF8.GetBytes(text)]);
            }

            WritePngChunk(png, "IDAT", compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
            WritePngChunk(png, "IEND", []);

            return png.ToArray();
        }

        /// <summary>
        /// The text the span, a PNG file, carries under <paramref name="keyword"/> in an uncompressed iTXt chunk, as
        /// <see cref="Png"/> writes it; null when there is none or the span is not a PNG.
        /// </summary>
        public string? PngText(string keyword)
        {
            if (!span.StartsWith(PngSignature))
                return null;

            // Each chunk: length, type, data, CRC.
            for (ReadOnlySpan<byte> rest = span[PngSignature.Length..]; rest.Length >= 12;)
            {
                int length = BinaryPrimitives.ReadInt32BigEndian(rest);
                if (length < 0 || length > rest.Length - 12)
                    return null;

                ReadOnlySpan<byte> data = rest.Slice(8, length);
                if (rest.Slice(4, 4).SequenceEqual("iTXt"u8) && ReadPngText(data, keyword) is { } text)
                    return text;

                rest = rest[(12 + length)..];
            }

            return null;
        }
    }

    /// <summary>
    /// The text of an iTXt chunk's data when it is uncompressed and under <paramref name="keyword"/>, else null.
    /// </summary>
    private static string? ReadPngText(ReadOnlySpan<byte> data, string keyword)
    {
        int keywordEnd = data.IndexOf((byte)0);
        if (keywordEnd < 0 || data.Length < keywordEnd + 3 || Encoding.Latin1.GetString(data[..keywordEnd]) != keyword)
            return null;

        if (data[keywordEnd + 1] != 0)
            return null; // compressed: not ours

        // The language tag and the translated keyword, each ended by a zero, come before the text.
        ReadOnlySpan<byte> rest = data[(keywordEnd + 3)..];
        for (int skipped = 0; skipped < 2; skipped++)
        {
            int end = rest.IndexOf((byte)0);
            if (end < 0)
                return null;

            rest = rest[(end + 1)..];
        }

        return Encoding.UTF8.GetString(rest);
    }

    /// <summary>
    /// Length, type, data, then a CRC over type and data; all integers big-endian.
    /// </summary>
    private static void WritePngChunk(Stream png, string type, ReadOnlySpan<byte> data)
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
