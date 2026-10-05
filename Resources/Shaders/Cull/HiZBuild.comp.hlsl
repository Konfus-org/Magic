// Builds six levels of the depth pyramid per dispatch, in a buffer so no texture is read and written in the
// same pass: levels FirstLevel .. FirstLevel + 5, FirstLevel = PassIteration() * 6, the first from the depth target (PassIteration() = 0)
// or from the level before it in the same buffer, the rest in group shared memory. Each group owns a
// 32 x 32 tile of the first level, so the five levels after it (16, 8, 4, 2, 1 texels a side) stay inside
// the group.
//
// Level sizes round up (Cull/Common.hlsli), so every texel covers 2 x 2 of the level below. A source
// coordinate past the edge is clamped to the edge: that repeats a texel already in the same 2 x 2 block (or,
// for a texel wholly past the edge, one its in-range neighbour already covers), which leaves every stored
// minimum alone without a branch. Reverse-Z, so "farthest" is min.

#include "Cull/Common.hlsli"

#define TILE_SIZE 32      // texels a side of the first level one group builds
#define GROUP_SIZE 16     // threads a side: each builds 2 x 2 texels of the tile
static const uint LevelsAfterFirst = 5u; // the tile halved down to one texel
static const uint LevelsPerPass = LevelsAfterFirst + 1u; // twin: Culling.HiZLevelsPerPass

Texture2D<float> Depth : READ(0);
SamplerState DepthSampler : SAMPLER(0);
RWStructuredBuffer<float> HiZ : WRITE(0);

groupshared float Tile[TILE_SIZE][TILE_SIZE];

// One texel of the depth target's view rectangle. Sampled (nearest, at the texel's centre) rather than
// loaded: a texture read without its sampler is reflected as a storage texture, which a depth target cannot
// be bound as.
float DepthTexel(uint2 texel, float2 depthTexelSize)
{
    uint2 clamped = min(texel, (uint2)ViewSize - 1u);
    return Depth.SampleLevel(DepthSampler, (ViewOrigin + float2(clamped) + 0.5) * depthTexelSize, 0.0);
}

// One texel of a pyramid level already in the buffer.
float LevelTexel(uint2 texel, uint levelOffset, uint2 levelSize)
{
    uint2 clamped = min(texel, levelSize - 1u);
    return HiZ[levelOffset + clamped.y * levelSize.x + clamped.x];
}

float Farthest(float first, float second, float third, float fourth)
{
    return min(min(first, second), min(third, fourth));
}

void Store(uint level, uint levelOffset, uint2 levelSize, uint2 texel, float depth)
{
    [branch] if (level < HiZLevelCount && all(texel < levelSize))
        HiZ[levelOffset + texel.y * levelSize.x + texel.x] = depth;
}

[numthreads(GROUP_SIZE, GROUP_SIZE, 1)]
void main(uint3 groupId : SV_GroupID, uint3 groupThreadId : SV_GroupThreadID)
{
    uint2 tileOrigin = groupId.xy * TILE_SIZE;
    uint2 thread = groupThreadId.xy;

    // Where the first level reads from and writes to, worked out once per thread.
    uint firstLevel = PassIteration() * LevelsPerPass;
    bool isFromDepth = firstLevel == 0u;
    uint sourceLevel = isFromDepth ? 0u : firstLevel - 1u;
    uint2 sourceSize = HiZLevelSize(sourceLevel);
    uint sourceOffset = HiZLevelOffset(sourceLevel);

    uint depthWidth;
    uint depthHeight;
    Depth.GetDimensions(depthWidth, depthHeight);
    float2 depthTexelSize = 1.0 / float2(depthWidth, depthHeight);

    uint level = firstLevel;
    uint2 levelSize = HiZLevelSize(level);
    uint levelOffset = isFromDepth ? 0u : sourceOffset + sourceSize.x * sourceSize.y;

    // The first level: each thread its 2 x 2 texels of the tile, each the farthest of 2 x 2 source texels.
    [unroll] for (uint corner = 0u; corner < 4u; corner++)
    {
        uint2 texel = thread * 2u + uint2(corner & 1u, corner >> 1u);
        uint2 source = (tileOrigin + texel) * 2u;

        float depth;
        [branch] if (isFromDepth)
        {
            depth = Farthest(
                DepthTexel(source, depthTexelSize),
                DepthTexel(source + uint2(1u, 0u), depthTexelSize),
                DepthTexel(source + uint2(0u, 1u), depthTexelSize),
                DepthTexel(source + uint2(1u, 1u), depthTexelSize));
        }
        else
        {
            depth = Farthest(
                LevelTexel(source, sourceOffset, sourceSize),
                LevelTexel(source + uint2(1u, 0u), sourceOffset, sourceSize),
                LevelTexel(source + uint2(0u, 1u), sourceOffset, sourceSize),
                LevelTexel(source + uint2(1u, 1u), sourceOffset, sourceSize));
        }

        Tile[texel.y][texel.x] = depth;
        Store(level, levelOffset, levelSize, tileOrigin + texel, depth);
    }

    // The next five from the tile, halving it each time: read the 2 x 2 block, wait, write it back smaller.
    [unroll] for (uint halving = 1u; halving <= LevelsAfterFirst; halving++)
    {
        level++;
        levelOffset += levelSize.x * levelSize.y;
        levelSize = (levelSize + 1u) >> 1u;

        uint side = (uint)TILE_SIZE >> halving;
        bool isActive = all(thread < side);
        GroupMemoryBarrierWithGroupSync();

        float depth = 1.0;
        [branch] if (isActive)
        {
            uint2 block = thread * 2u;
            depth = Farthest(
                Tile[block.y][block.x],
                Tile[block.y][block.x + 1u],
                Tile[block.y + 1u][block.x],
                Tile[block.y + 1u][block.x + 1u]);
        }

        GroupMemoryBarrierWithGroupSync();
        [branch] if (isActive)
        {
            Tile[thread.y][thread.x] = depth;
            Store(level, levelOffset, levelSize, (tileOrigin >> halving) + thread, depth);
        }
    }
}
