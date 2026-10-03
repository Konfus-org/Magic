using System.Numerics;

namespace Magic.Utils;

/// <summary>
/// Sub-allocates ranges of one big buffer: Sebastian Aaltonen's OffsetAllocator (MIT), a two-level
/// segregated fit with 256 size bins, ported. Allocation and free are O(1); neighbours merge on free.
/// Used for the mega vertex and index buffers and the visible-id regions.
/// </summary>
public sealed class RangeAllocator
{
    private const int TopBins = 32;

    private const int LeafBins = 8;

    private const uint Unused = 0xFFFFFFFF;

    private readonly byte[] _usedBins = new byte[TopBins];

    private readonly uint[] _binIndices = new uint[TopBins * LeafBins];

    private readonly Node[] _nodes;

    private readonly uint[] _freeNodes;

    private uint _usedBinsTop;

    private uint _freeOffset;

    public RangeAllocator(uint size, uint maxAllocations)
    {
        _nodes = new Node[maxAllocations];
        _freeNodes = new uint[maxAllocations];
        _freeOffset = maxAllocations - 1;
        Array.Fill(_binIndices, Unused);
        for (uint i = 0; i < maxAllocations; i++)
            _freeNodes[i] = maxAllocations - i - 1;

        InsertNodeIntoBin(size, 0);
    }

    /// <summary>
    /// Bytes still free (fragmentation may keep some of them unusable for a given request).
    /// </summary>
    public uint FreeStorage { get; private set; }

    public Allocation Allocate(uint size)
    {
        if (size == 0)
            size = 1;

        if (_freeOffset == 0)
            return Allocation.None; // out of nodes

        uint minBinIndex = Bin(size, roundUp: true);
        uint minTopBinIndex = minBinIndex >> 3;
        uint minLeafBinIndex = minBinIndex & 7;
        uint topBinIndex = minTopBinIndex;
        uint leafBinIndex = Unused;

        if ((_usedBinsTop & (1u << (int)topBinIndex)) != 0)
            leafBinIndex = FindLowestSetBitAfter(_usedBins[topBinIndex], minLeafBinIndex);

        if (leafBinIndex == Unused)
        {
            topBinIndex = FindLowestSetBitAfter(_usedBinsTop, minTopBinIndex + 1);
            if (topBinIndex == Unused)
                return Allocation.None; // out of space

            leafBinIndex = (uint)BitOperations.TrailingZeroCount(_usedBins[topBinIndex]);
        }

        uint nodeIndex = _binIndices[(topBinIndex << 3) | leafBinIndex];
        Unlink(nodeIndex);

        ref Node node = ref _nodes[nodeIndex];
        uint remainder = node.Size - size;
        node.Size = size;
        node.Used = true;
        if (remainder > 0)
        {
            uint newNodeIndex = InsertNodeIntoBin(remainder, node.Offset + size);
            if (node.NeighborNext != Unused)
                _nodes[node.NeighborNext].NeighborPrev = newNodeIndex;
            _nodes[newNodeIndex].NeighborPrev = nodeIndex;
            _nodes[newNodeIndex].NeighborNext = node.NeighborNext;
            node.NeighborNext = newNodeIndex;
        }

        return new Allocation(node.Offset, nodeIndex);
    }

    public void Free(Allocation allocation)
    {
        if (allocation.IsNone)
            return;

        uint nodeIndex = allocation.Node;
        ref Node node = ref _nodes[nodeIndex];
        if (!node.Used)
            throw new InvalidOperationException("double free of a RangeAllocator range.");

        uint offset = node.Offset;
        uint size = node.Size;
        if (node.NeighborPrev != Unused && !_nodes[node.NeighborPrev].Used)
        {
            uint prevIndex = node.NeighborPrev;
            ref Node prev = ref _nodes[prevIndex];
            offset = prev.Offset;
            size += prev.Size;
            Unlink(prevIndex);
            _freeNodes[++_freeOffset] = prevIndex;
            node.NeighborPrev = prev.NeighborPrev;
        }

        if (node.NeighborNext != Unused && !_nodes[node.NeighborNext].Used)
        {
            uint nextIndex = node.NeighborNext;
            ref Node next = ref _nodes[nextIndex];
            size += next.Size;
            Unlink(nextIndex);
            _freeNodes[++_freeOffset] = nextIndex;
            node.NeighborNext = next.NeighborNext;
        }

        uint neighborNext = node.NeighborNext;
        uint neighborPrev = node.NeighborPrev;
        _freeNodes[++_freeOffset] = nodeIndex;
        uint combined = InsertNodeIntoBin(size, offset);
        if (neighborNext != Unused)
        {
            _nodes[combined].NeighborNext = neighborNext;
            _nodes[neighborNext].NeighborPrev = combined;
        }

        if (neighborPrev != Unused)
        {
            _nodes[combined].NeighborPrev = neighborPrev;
            _nodes[neighborPrev].NeighborNext = combined;
        }
    }

    /// <summary>
    /// A free node for the range, at the head of its size bin.
    /// </summary>
    private uint InsertNodeIntoBin(uint size, uint offset)
    {
        uint binIndex = Bin(size, roundUp: false);
        uint topNodeIndex = _binIndices[binIndex];
        if (topNodeIndex == Unused)
        {
            _usedBins[binIndex >> 3] |= (byte)(1 << (int)(binIndex & 7));
            _usedBinsTop |= 1u << (int)(binIndex >> 3);
        }

        uint nodeIndex = _freeNodes[_freeOffset--];
        _nodes[nodeIndex] = new Node { Offset = offset, Size = size, BinNext = topNodeIndex, BinPrev = Unused, NeighborPrev = Unused, NeighborNext = Unused };
        if (topNodeIndex != Unused)
            _nodes[topNodeIndex].BinPrev = nodeIndex;
        _binIndices[binIndex] = nodeIndex;
        FreeStorage += size;

        return nodeIndex;
    }

    /// <summary>
    /// Takes a free node out of its size bin; the node itself is the caller's to reuse or recycle.
    /// </summary>
    private void Unlink(uint nodeIndex)
    {
        ref Node node = ref _nodes[nodeIndex];
        if (node.BinNext != Unused)
            _nodes[node.BinNext].BinPrev = node.BinPrev;

        if (node.BinPrev != Unused)
            _nodes[node.BinPrev].BinNext = node.BinNext;
        else
        {
            uint binIndex = Bin(node.Size, roundUp: false);
            _binIndices[binIndex] = node.BinNext;
            if (node.BinNext == Unused)
            {
                _usedBins[binIndex >> 3] &= (byte)~(1 << (int)(binIndex & 7));
                if (_usedBins[binIndex >> 3] == 0)
                    _usedBinsTop &= ~(1u << (int)(binIndex >> 3));
            }
        }

        FreeStorage -= node.Size;
    }

    private static uint FindLowestSetBitAfter(uint bitMask, uint startBitIndex)
    {
        uint bitsAfter = bitMask & ~((1u << (int)startBitIndex) - 1);
        return bitsAfter == 0 ? Unused : (uint)BitOperations.TrailingZeroCount(bitsAfter);
    }

    /// <summary>
    /// The size bin of <paramref name="size"/>: an 8-bit "small float" with 3 mantissa bits, so bins are ~12% apart.
    /// Rounded down it is the bin a free range of that size sits in; rounded up, the first bin whose every range fits it.
    /// </summary>
    private static uint Bin(uint size, bool roundUp)
    {
        // Below the mantissa range the exponent is 0 and the bin is the size itself.
        if (size < 8)
            return size;

        int mantissaStartBit = 31 - BitOperations.LeadingZeroCount(size) - 3;
        uint bin = ((uint)(mantissaStartBit + 1) << 3) | ((size >> mantissaStartBit) & 7);
        if (roundUp && (size & ((1u << mantissaStartBit) - 1)) != 0)
            bin++;

        return bin;
    }

    public readonly record struct Allocation(uint Offset, uint Node)
    {
        public static readonly Allocation None = new(uint.MaxValue, uint.MaxValue);

        public bool IsNone => Offset == uint.MaxValue;
    }

    private struct Node
    {
        public uint Offset;

        public uint Size;

        public uint BinPrev, BinNext;

        public uint NeighborPrev, NeighborNext;

        public bool Used;
    }
}
