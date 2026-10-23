// Sorts the view's 8 x 8 tiles by the surfaces their pixels are shaded with, a group a tile: each pixel's triangle (the
// visibility buffer) names its piece, its instance and its cluster's or card's material slot, whose row names its
// resolve bin. Each bin the tile holds is noted once, and the tile added to that bin's run of the tile list, so a
// surface's resolve draws a quad over each tile it is in and no other. A tile's entry is its place, 12 bits each way,
// and its bin in the top 8.

#include "Include/Failure.hlsli"
#include "Rendering/Include/Culling.hlsli"

struct ClassifyConstants
{
    uint view;
    uint visibility;         // the visibility buffer
    uint pieces;
    uint tileCounts;         // the tiles each resolve bin holds
    uint tileList;           // each resolve bin's tiles, a run of as many as the view has
};

CONSTANTS(ClassifyConstants, c);

static const uint TileSize = 8u;

groupshared uint BinMask[8];

[numthreads(8, 8, 1)]
void main(uint3 tile : SV_GroupID, uint3 id : SV_DispatchThreadID, uint thread : SV_GroupIndex)
{
    LoadView(c.view);
    if (thread < 8u)
    {
        BinMask[thread] = 0u;
    }

    GroupMemoryBarrierWithGroupSync();
    uint visible = all(id.xy < (uint2)View.viewSize) ? UINT_TEXTURE(c.visibility).Load(int3(id.xy, 0)) : 0u;
    if (visible != 0u)
    {
        uint3 piece = BUFFER(c.pieces).Load3(((visible - 1u) >> 5) * 12u);
        GpuInstance instance = LoadInstance(piece.x);
        uint slot = (piece.y & CardBit) != 0u ? LoadCard(piece.y & ~CardBit).slot : ClusterSlot(LoadCluster(piece.y >> 2));
        uint material = InstanceMaterial(instance, slot);

        // A row past the materials reads nothing meant: raised, and binned with the first.
        bool isInRange = material < CountOf(BUFFER(View.materialBins), 4u);
        [branch] if (!isInRange)
        {
            RaiseFailure(BUFFER(View.failures), FailureMaterialRange);
        }

        uint bin = isInRange ? (LoadMaterialBins(material) >> 16) & 0xFFu : 0u;
        InterlockedOr(BinMask[bin >> 5], 1u << (bin & 31u));
    }

    GroupMemoryBarrierWithGroupSync();
    uint tiles = ((uint)View.viewSize.x + TileSize - 1u) / TileSize * (((uint)View.viewSize.y + TileSize - 1u) / TileSize);
    [loop] for (uint bin = thread * 4u; bin < thread * 4u + 4u; bin++)
    {
        if ((BinMask[bin >> 5] & (1u << (bin & 31u))) == 0u)
        {
            continue;
        }

        uint at;
        BUFFER(c.tileCounts).InterlockedAdd(bin * 4u, 1u, at);
        if (bin * tiles + at < CountOf(BUFFER(c.tileList), 4u))
        {
            BUFFER(c.tileList).Store((bin * tiles + at) * 4u, tile.x | (tile.y << 12) | (bin << 24));
        }
    }
}
