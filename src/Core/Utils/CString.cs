using System.Runtime.InteropServices;
using System.Text;

namespace Magic.Utils;

/// <summary>
/// Text at the C boundary: a C string is UTF-8 bytes ending in a zero, which a native API takes and gives as
/// <c>byte*</c>.
/// </summary>
public static class CString
{
    /// <summary>
    /// The text at a C string, or empty for a null one.
    /// </summary>
    public static unsafe string Read(byte* text) => Marshal.PtrToStringUTF8((nint)text) ?? string.Empty;

    /// <summary>
    /// The text as a zero-terminated UTF-8 string, to pin (<c>fixed</c>) for as long as the C call reads it.
    /// </summary>
    public static byte[] ToBytes(string text) => Encoding.UTF8.GetBytes(text + '\0');
}
