namespace Magic.Utils;

/// <summary>
/// Sub-allocates ranges of one big buffer: Sebastian Aaltonen's OffsetAllocator (MIT), a two-level
/// segregated fit with 256 size bins, ported. Allocation and free are O(1); neighbours merge on free.
/// Used for the mega vertex and index buffers and the visible-id regions.
/// </summary>
internal sealed class OffsetAllocator
{
    private const int TopBins = 32;

    private const int LeafBins = 8;

    private const int Bins = TopBins * LeafBins;

    private const uint Unused = 0xFFFFFFFF;

    private readonly byte[] _usedBins = new byte[TopBins];

    private readonly uint[] _binIndices = new uint[Bins];

    private uint _usedBinsTop;

    private Node[] _nodes;

    private uint[] _freeNodes;

    private uint _freeOffset;

    public OffsetAllocator(uint size, uint maxAllocations = 128 * 1024)
    {
        Size = size;
        _nodes = new Node[maxAllocations];
        _freeNodes = new uint[maxAllocations];
        Reset();
    }

    public uint Size { get; }

    /// <summary>Bytes still free (fragmentation may keep some of them unusable for a given request).</summary>
    public uint FreeStorage { get; private set; }

    public void Reset()
    {
        FreeStorage = 0;
        _usedBinsTop = 0;
        _freeOffset = (uint)_nodes.Length - 1;
        Array.Clear(_usedBins);
        Array.Fill(_binIndices, Unused);
        for (uint i = 0; i < _nodes.Length; i++)
        {
            _nodes[i] = default;
            _freeNodes[i] = (uint)_nodes.Length - i - 1;
        }

        InsertNodeIntoBin(Size, 0);
    }

    public Allocation Allocate(uint size)
    {
        if (size == 0)
            size = 1;

        if (_freeOffset == 0)
            return Allocation.None; // out of nodes

        uint minBinIndex = SmallFloat.UintToFloatRoundUp(size);
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

            leafBinIndex = TrailingZeros(_usedBins[topBinIndex]);
        }

        uint binIndex = (topBinIndex << 3) | leafBinIndex;
        uint nodeIndex = _binIndices[binIndex];
        ref Node node = ref _nodes[nodeIndex];
        uint nodeTotalSize = node.Size;
        node.Size = size;
        node.Used = true;
        _binIndices[binIndex] = node.BinNext;
        if (node.BinNext != Unused)
            _nodes[node.BinNext].BinPrev = Unused;
        FreeStorage -= nodeTotalSize;

        if (_binIndices[binIndex] == Unused)
        {
            _usedBins[topBinIndex] &= (byte)~(1 << (int)leafBinIndex);
            if (_usedBins[topBinIndex] == 0)
                _usedBinsTop &= ~(1u << (int)topBinIndex);
        }

        uint remainder = nodeTotalSize - size;
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

        uint nodeIndex = allocation.Metadata;
        ref Node node = ref _nodes[nodeIndex];
        if (!node.Used)
            throw new InvalidOperationException("double free of an OffsetAllocator range.");

        uint offset = node.Offset;
        uint size = node.Size;
        if (node.NeighborPrev != Unused && !_nodes[node.NeighborPrev].Used)
        {
            ref Node prev = ref _nodes[node.NeighborPrev];
            offset = prev.Offset;
            size += prev.Size;
            RemoveNodeFromBin(node.NeighborPrev);
            node.NeighborPrev = prev.NeighborPrev;
        }

        if (node.NeighborNext != Unused && !_nodes[node.NeighborNext].Used)
        {
            ref Node next = ref _nodes[node.NeighborNext];
            size += next.Size;
            RemoveNodeFromBin(node.NeighborNext);
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

    private uint InsertNodeIntoBin(uint size, uint offset)
    {
        uint binIndex = SmallFloat.UintToFloatRoundDown(size);
        uint topBinIndex = binIndex >> 3;
        uint leafBinIndex = binIndex & 7;
        if (_binIndices[binIndex] == Unused)
        {
            _usedBins[topBinIndex] |= (byte)(1 << (int)leafBinIndex);
            _usedBinsTop |= 1u << (int)topBinIndex;
        }

        uint topNodeIndex = _binIndices[binIndex];
        uint nodeIndex = _freeNodes[_freeOffset--];
        _nodes[nodeIndex] = new Node { Offset = offset, Size = size, BinNext = topNodeIndex, BinPrev = Unused, NeighborPrev = Unused, NeighborNext = Unused };
        if (topNodeIndex != Unused)
            _nodes[topNodeIndex].BinPrev = nodeIndex;
        _binIndices[binIndex] = nodeIndex;
        FreeStorage += size;

        return nodeIndex;
    }

    private void RemoveNodeFromBin(uint nodeIndex)
    {
        ref Node node = ref _nodes[nodeIndex];
        if (node.BinPrev != Unused)
        {
            _nodes[node.BinPrev].BinNext = node.BinNext;
            if (node.BinNext != Unused)
                _nodes[node.BinNext].BinPrev = node.BinPrev;
        }
        else
        {
            uint binIndex = SmallFloat.UintToFloatRoundDown(node.Size);
            uint topBinIndex = binIndex >> 3;
            uint leafBinIndex = binIndex & 7;
            _binIndices[binIndex] = node.BinNext;
            if (node.BinNext != Unused)
                _nodes[node.BinNext].BinPrev = Unused;
            if (_binIndices[binIndex] == Unused)
            {
                _usedBins[topBinIndex] &= (byte)~(1 << (int)leafBinIndex);
                if (_usedBins[topBinIndex] == 0)
                    _usedBinsTop &= ~(1u << (int)topBinIndex);
            }
        }

        _freeNodes[++_freeOffset] = nodeIndex;
        FreeStorage -= node.Size;
    }

    private static uint FindLowestSetBitAfter(uint bitMask, uint startBitIndex)
    {
        uint maskBeforeStartIndex = (1u << (int)startBitIndex) - 1;
        uint maskAfterStartIndex = ~maskBeforeStartIndex;
        uint bitsAfter = bitMask & maskAfterStartIndex;
        return bitsAfter == 0 ? Unused : TrailingZeros(bitsAfter);
    }

    private static uint TrailingZeros(uint v)
    {
        return (uint)System.Numerics.BitOperations.TrailingZeroCount(v);
    }

    public readonly record struct Allocation(uint Offset, uint Metadata)
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

    /// <summary>The 8-bit "small float" bin encoding: 3 mantissa bits, so bins are ~12% apart.</summary>
    private static class SmallFloat
    {
        private const uint MantissaBits = 3;

        private const uint MantissaValue = 1 << (int)MantissaBits;

        private const uint MantissaMask = MantissaValue - 1;

        public static uint UintToFloatRoundUp(uint size)
        {
            // Below the mantissa range the exponent is 0 and the bin is the size itself.
            if (size < MantissaValue)
                return size;

            uint leadingZeros = (uint)System.Numerics.BitOperations.LeadingZeroCount(size);
            uint highestSetBit = 31 - leadingZeros;
            uint mantissaStartBit = highestSetBit - MantissaBits;
            uint exp = mantissaStartBit + 1;
            uint mantissa = (size >> (int)mantissaStartBit) & MantissaMask;
            uint lowBitsMask = (1u << (int)mantissaStartBit) - 1;
            if ((size & lowBitsMask) != 0)
                mantissa++;

            return (exp << (int)MantissaBits) + mantissa;
        }

        public static uint UintToFloatRoundDown(uint size)
        {
            if (size < MantissaValue)
                return size;

            uint leadingZeros = (uint)System.Numerics.BitOperations.LeadingZeroCount(size);
            uint highestSetBit = 31 - leadingZeros;
            uint mantissaStartBit = highestSetBit - MantissaBits;
            uint exp = mantissaStartBit + 1;
            uint mantissa = (size >> (int)mantissaStartBit) & MantissaMask;

            return (exp << (int)MantissaBits) | mantissa;
        }
    }
}
