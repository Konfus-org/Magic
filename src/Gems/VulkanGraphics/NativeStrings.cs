using System.Runtime.InteropServices;
using System.Text;

namespace VulkanGraphics;

/// <summary>
/// Strings as Vulkan takes them: an array of pointers to UTF-8 bytes each ending in 0, held in native memory until
/// disposed, which is after the call that reads them.
/// </summary>
internal sealed unsafe class NativeStrings : IDisposable
{
    public NativeStrings(IReadOnlyList<string> strings)
    {
        Count = (uint)strings.Count;
        Pointers = (byte**)NativeMemory.Alloc((nuint)(sizeof(byte*) * Math.Max(1, strings.Count)));
        for (int i = 0; i < strings.Count; i++)
        {
            int bytes = Encoding.UTF8.GetByteCount(strings[i]);
            byte* text = (byte*)NativeMemory.Alloc((nuint)bytes + 1);
            Encoding.UTF8.GetBytes(strings[i], new Span<byte>(text, bytes));
            text[bytes] = 0;
            Pointers[i] = text;
        }
    }

    public uint Count { get; }

    public byte** Pointers { get; }

    /// <summary>
    /// The text at <paramref name="text"/> up to its 0, as Vulkan hands names back; empty for null.
    /// </summary>
    public static string Read(byte* text) =>
        text == null ? string.Empty : Marshal.PtrToStringUTF8((nint)text) ?? string.Empty;

    public void Dispose()
    {
        for (uint i = 0; i < Count; i++)
        {
            NativeMemory.Free(Pointers[i]);
        }

        NativeMemory.Free(Pointers);
    }
}
