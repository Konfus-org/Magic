using System.Runtime.InteropServices;
using System.Text;

namespace Magic.ImGuiDebugUI;

/// <summary>
/// Text for ImGui as the NUL-terminated UTF-8 it reads. Strings go into blocks of unmanaged memory that never move,
/// so a pointer handed out stays good for the rest of the frame however many follow it, and the blocks are reused
/// from the start when the next frame begins: ImGui copies or hashes what it is handed within the call.
/// </summary>
internal sealed unsafe class TextArena : IDisposable
{
    private const int BlockBytes = 64 * 1024;
    private readonly List<(nint Start, int Bytes)> _blocks = [];
    private int _block = -1;
    private int _used;

    public void Dispose()
    {
        foreach ((nint start, _) in _blocks)
        {
            NativeMemory.Free((void*)start);
        }

        _blocks.Clear();
        Reset();
    }

    /// <summary>
    /// The text as a C string, good until <see cref="Reset"/>.
    /// </summary>
    public byte* Of(string text)
    {
        int most = Encoding.UTF8.GetMaxByteCount(text.Length) + 1;
        if (_block < 0 || _used + most > _blocks[_block].Bytes)
        {
            _block++;
            _used = 0;
            if (_block == _blocks.Count || _blocks[_block].Bytes < most)
            {
                int bytes = Math.Max(BlockBytes, most);
                if (_block < _blocks.Count)
                {
                    NativeMemory.Free((void*)_blocks[_block].Start);
                    _blocks[_block] = ((nint)NativeMemory.Alloc((nuint)bytes), bytes);
                }
                else
                {
                    _blocks.Add(((nint)NativeMemory.Alloc((nuint)bytes), bytes));
                }
            }
        }

        byte* at = (byte*)_blocks[_block].Start + _used;
        int written = Encoding.UTF8.GetBytes(text, new Span<byte>(at, most - 1));
        at[written] = 0;
        _used += written + 1;
        return at;
    }

    /// <summary>
    /// Lets every string go, for the next frame's; the blocks stay for it.
    /// </summary>
    public void Reset()
    {
        _block = -1;
        _used = 0;
    }
}
