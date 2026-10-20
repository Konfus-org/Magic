using System.Runtime.InteropServices;
using System.Text;

namespace Magic.Contexts.Utils;

/// <summary>
/// Text at the C boundary: a C string is UTF-8 bytes ending in a zero, which a binding takes and gives as
/// <c>byte*</c>, and what a gem reads from or hands to one goes through here.
/// </summary>
public static class CString
{
    /// <summary>
    /// The text at a C string, or empty for a null one.
    /// </summary>
    public static unsafe string Read(byte* text) => text is null ? string.Empty : Marshal.PtrToStringUTF8((nint)text) ?? string.Empty;

    /// <summary>
    /// The text as a zero-terminated UTF-8 string, to pin (<c>fixed</c>) for as long as the C call reads it.
    /// </summary>
    public static byte[] Bytes(string text)
    {
        byte[] bytes = new byte[Encoding.UTF8.GetByteCount(text) + 1];
        Encoding.UTF8.GetBytes(text, bytes);
        return bytes;
    }
}
