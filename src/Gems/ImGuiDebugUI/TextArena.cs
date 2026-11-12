using System.Runtime.InteropServices;
using System.Text;

namespace Magic.ImGuiDebugUI;

/// <summary>
/// Holds one frame's text for ImGui as the NUL-terminated UTF-8 that it reads.
/// </summary>
/// <remarks>
/// A pointer handed out stays good until <see cref="Reset"/>, however many strings follow it. The blocks are used again
/// from the start each frame, because ImGui copies or hashes what it gets within the call.
/// </remarks>
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
    internal byte* Add(string text)
    {
        // Room for the most bytes the text could take, and its NUL. When what is left of the block is too little, it goes
        // into the next, which is made, or made again larger, when it is too small for the text.
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
    internal void Reset()
    {
        _block = -1;
        _used = 0;
    }
}
