using System.Buffers.Binary;

namespace Magic.DxcShaderCompiler;

/// <summary>
/// The hash Direct3D 12 checks a DXIL container for before it accepts the shader, written where DXC's validator
/// writes it: MD5 over the container past the hash field, with the last block laid out as the validator lays it, the
/// bit count first and an odd marker last where MD5 puts its length. The algorithm is Microsoft's, published in
/// DirectXShaderCompiler (DxilHash.cpp) so shaders can be signed on any platform; the container is otherwise as DXC
/// made it.
/// </summary>
internal static class DxilHash
{
    /// <summary>The hash field follows the four-character code.</summary>
    private const int HashOffset = 4;

    /// <summary>What is hashed: from the version field, after the hash, to the end.</summary>
    private const int HashedFrom = 20;

    public static void Sign(Span<byte> container)
    {
        if (container.Length < HashedFrom + 8)
        {
            return;
        }

        Span<byte> digest = stackalloc byte[16];
        Hash(container[HashedFrom..], digest);
        digest.CopyTo(container.Slice(HashOffset, 16));
    }

    private static void Hash(ReadOnlySpan<byte> data, Span<byte> digest)
    {
        int count = data.Length;
        int leftOver = count & 0x3f;
        bool twoRows = leftOver >= 56;
        int padAmount = twoRows ? 120 - leftOver : 56 - leftOver;
        int blocks = (count + padAmount + 8) >> 6;
        int nextEnd = twoRows ? blocks - 2 : blocks - 1;
        Span<uint> state = [0x67452301, 0xefcdab89, 0x98badcfe, 0x10325476];
        Span<byte> last = stackalloc byte[64];
        Span<uint> x = stackalloc uint[16];
        for (int i = 0, offset = 0; i < blocks; i++, offset += 64)
        {
            scoped ReadOnlySpan<byte> block;
            if (i == nextEnd)
            {
                // The padding is MD5's, 0x80 then zeros; where it starts and what surrounds it is the validator's.
                last.Clear();
                int remainder = count - offset;
                if (!twoRows)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(last, (uint)count << 3);
                    data.Slice(offset, remainder).CopyTo(last[4..]);
                    last[4 + remainder] = 0x80;
                    BinaryPrimitives.WriteUInt32LittleEndian(last[60..], 1 | ((uint)count << 1));
                }
                else if (i == blocks - 2)
                {
                    data.Slice(offset, remainder).CopyTo(last);
                    last[remainder] = 0x80;
                    nextEnd = blocks - 1;
                }
                else
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(last, (uint)count << 3);
                    BinaryPrimitives.WriteUInt32LittleEndian(last[60..], 1 | ((uint)count << 1));
                }

                block = last;
            }
            else
            {
                block = data.Slice(offset, 64);
            }

            for (int word = 0; word < 16; word++)
            {
                x[word] = BinaryPrimitives.ReadUInt32LittleEndian(block[(word * 4)..]);
            }

            Round(state, x);
        }

        for (int i = 0; i < 4; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(digest[(i * 4)..], state[i]);
        }
    }

    /// <summary>One MD5 block: the four rounds, in RFC 1321's order and constants.</summary>
    private static void Round(Span<uint> state, ReadOnlySpan<uint> x)
    {
        uint a = state[0];
        uint b = state[1];
        uint c = state[2];
        uint d = state[3];

        FF(ref a, b, c, d, x[0], 7, 0xd76aa478);
        FF(ref d, a, b, c, x[1], 12, 0xe8c7b756);
        FF(ref c, d, a, b, x[2], 17, 0x242070db);
        FF(ref b, c, d, a, x[3], 22, 0xc1bdceee);
        FF(ref a, b, c, d, x[4], 7, 0xf57c0faf);
        FF(ref d, a, b, c, x[5], 12, 0x4787c62a);
        FF(ref c, d, a, b, x[6], 17, 0xa8304613);
        FF(ref b, c, d, a, x[7], 22, 0xfd469501);
        FF(ref a, b, c, d, x[8], 7, 0x698098d8);
        FF(ref d, a, b, c, x[9], 12, 0x8b44f7af);
        FF(ref c, d, a, b, x[10], 17, 0xffff5bb1);
        FF(ref b, c, d, a, x[11], 22, 0x895cd7be);
        FF(ref a, b, c, d, x[12], 7, 0x6b901122);
        FF(ref d, a, b, c, x[13], 12, 0xfd987193);
        FF(ref c, d, a, b, x[14], 17, 0xa679438e);
        FF(ref b, c, d, a, x[15], 22, 0x49b40821);

        GG(ref a, b, c, d, x[1], 5, 0xf61e2562);
        GG(ref d, a, b, c, x[6], 9, 0xc040b340);
        GG(ref c, d, a, b, x[11], 14, 0x265e5a51);
        GG(ref b, c, d, a, x[0], 20, 0xe9b6c7aa);
        GG(ref a, b, c, d, x[5], 5, 0xd62f105d);
        GG(ref d, a, b, c, x[10], 9, 0x02441453);
        GG(ref c, d, a, b, x[15], 14, 0xd8a1e681);
        GG(ref b, c, d, a, x[4], 20, 0xe7d3fbc8);
        GG(ref a, b, c, d, x[9], 5, 0x21e1cde6);
        GG(ref d, a, b, c, x[14], 9, 0xc33707d6);
        GG(ref c, d, a, b, x[3], 14, 0xf4d50d87);
        GG(ref b, c, d, a, x[8], 20, 0x455a14ed);
        GG(ref a, b, c, d, x[13], 5, 0xa9e3e905);
        GG(ref d, a, b, c, x[2], 9, 0xfcefa3f8);
        GG(ref c, d, a, b, x[7], 14, 0x676f02d9);
        GG(ref b, c, d, a, x[12], 20, 0x8d2a4c8a);

        HH(ref a, b, c, d, x[5], 4, 0xfffa3942);
        HH(ref d, a, b, c, x[8], 11, 0x8771f681);
        HH(ref c, d, a, b, x[11], 16, 0x6d9d6122);
        HH(ref b, c, d, a, x[14], 23, 0xfde5380c);
        HH(ref a, b, c, d, x[1], 4, 0xa4beea44);
        HH(ref d, a, b, c, x[4], 11, 0x4bdecfa9);
        HH(ref c, d, a, b, x[7], 16, 0xf6bb4b60);
        HH(ref b, c, d, a, x[10], 23, 0xbebfbc70);
        HH(ref a, b, c, d, x[13], 4, 0x289b7ec6);
        HH(ref d, a, b, c, x[0], 11, 0xeaa127fa);
        HH(ref c, d, a, b, x[3], 16, 0xd4ef3085);
        HH(ref b, c, d, a, x[6], 23, 0x04881d05);
        HH(ref a, b, c, d, x[9], 4, 0xd9d4d039);
        HH(ref d, a, b, c, x[12], 11, 0xe6db99e5);
        HH(ref c, d, a, b, x[15], 16, 0x1fa27cf8);
        HH(ref b, c, d, a, x[2], 23, 0xc4ac5665);

        II(ref a, b, c, d, x[0], 6, 0xf4292244);
        II(ref d, a, b, c, x[7], 10, 0x432aff97);
        II(ref c, d, a, b, x[14], 15, 0xab9423a7);
        II(ref b, c, d, a, x[5], 21, 0xfc93a039);
        II(ref a, b, c, d, x[12], 6, 0x655b59c3);
        II(ref d, a, b, c, x[3], 10, 0x8f0ccc92);
        II(ref c, d, a, b, x[10], 15, 0xffeff47d);
        II(ref b, c, d, a, x[1], 21, 0x85845dd1);
        II(ref a, b, c, d, x[8], 6, 0x6fa87e4f);
        II(ref d, a, b, c, x[15], 10, 0xfe2ce6e0);
        II(ref c, d, a, b, x[6], 15, 0xa3014314);
        II(ref b, c, d, a, x[13], 21, 0x4e0811a1);
        II(ref a, b, c, d, x[4], 6, 0xf7537e82);
        II(ref d, a, b, c, x[11], 10, 0xbd3af235);
        II(ref c, d, a, b, x[2], 15, 0x2ad7d2bb);
        II(ref b, c, d, a, x[9], 21, 0xeb86d391);

        // MD5 arithmetic wraps, whatever the build checks elsewhere.
        state[0] = unchecked(state[0] + a);
        state[1] = unchecked(state[1] + b);
        state[2] = unchecked(state[2] + c);
        state[3] = unchecked(state[3] + d);
    }

    private static void FF(ref uint a, uint b, uint c, uint d, uint x, int s, uint ac)
        => a = unchecked(b + uint.RotateLeft(unchecked(a + ((b & c) | (~b & d)) + x + ac), s));

    private static void GG(ref uint a, uint b, uint c, uint d, uint x, int s, uint ac)
        => a = unchecked(b + uint.RotateLeft(unchecked(a + ((b & d) | (c & ~d)) + x + ac), s));

    private static void HH(ref uint a, uint b, uint c, uint d, uint x, int s, uint ac)
        => a = unchecked(b + uint.RotateLeft(unchecked(a + (b ^ c ^ d) + x + ac), s));

    private static void II(ref uint a, uint b, uint c, uint d, uint x, int s, uint ac)
        => a = unchecked(b + uint.RotateLeft(unchecked(a + (c ^ (b | ~d)) + x + ac), s));
}
