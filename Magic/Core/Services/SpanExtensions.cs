using System.Text;

namespace Magic.Extensions;

/// <summary>
/// Raw bytes read from a file, made ready to parse.
/// </summary>
public static class SpanExtensions
{
    extension(ReadOnlySpan<byte> span)
    {
        /// <summary>
        /// The bytes after a UTF-8 byte order mark, when they start with one; JSON readers take none.
        /// </summary>
        public ReadOnlySpan<byte> WithoutBom()
        {
            ReadOnlySpan<byte> bom = Encoding.UTF8.Preamble;
            return span.StartsWith(bom) ? span[bom.Length..] : span;
        }
    }
}
