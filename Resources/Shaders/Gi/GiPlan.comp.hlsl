// Plans the GI clipmap for the frame, on the GPU, into the GI state buffer: one thread. Which level is rebuilt
// (none on the frames between rebuilds, framesPerRebuild apart; of the rebuilds, the finest every other one, the
// coarser ones in turn between), where every level sits (the main camera at its centre, snapped to its own voxel
// grid so the volume only ever moves by whole voxels), how many voxels the rebuilt level's origin moved since it
// was last built (so its old light is found again), and which of the two light sets the rebuilt level's new light
// goes into (its parity flips). A change to the layout (the hash in the state's generation) starts every level over.

#include "Include/Frame.hlsli"
#include "Include/Structs.hlsli"

struct PassParams
{
    uint levels = 3;       // the same numbers as the volumes' creates: levels, and voxels a side
    uint resolution = 48;
    float voxelSize = 1.0;
    uint levelScale = 4;
    uint framesPerRebuild = 2; // a level is rebuilt every this many frames; the frames between rebuild nothing
};

RWStructuredBuffer<GpuGiState> GiState : WRITE(0);

float3 LevelOrigin(float3 camera, uint resolution, float voxel)
{
    float3 corner = camera - (float)resolution * voxel * 0.5;
    return floor(corner / voxel) * voxel;
}

// No level: the frame rebuilds nothing, and every GI pass after the plan returns at once.
static const uint NoLevel = 0xFFFFFFFFu;

uint UpdateLevel(uint frame, uint levels)
{
    return (levels <= 1u || (frame & 1u) == 0u) ? 0u : ((frame / 2u) % (levels - 1u)) + 1u;
}

// The state is read and written a field at a time, never copied whole: D3D12's compiler scrambled the origins
// array of a copied state.
[numthreads(1, 1, 1)]
void main()
{
    PassParams passParams = LoadPassParams();
    uint levels = clamp(passParams.levels, 1u, 4u);
    uint generation = (levels * 73856093u) ^ (passParams.resolution * 19349663u) ^ (asuint(passParams.voxelSize) * 83492791u) ^ (passParams.levelScale * 2654435761u) ^ 0x9E3779B9u;
    uint valid = GiState[0].valid;
    uint4 parity = GiState[0].parity;
    [branch] if (GiState[0].extra.x != generation)
    {
        valid = 0u;
        parity = uint4(0u, 0u, 0u, 0u);
        GiState[0].extra.x = generation;
    }

    uint every = max(passParams.framesPerRebuild, 1u);
    uint level = FrameNumber % every == 0u ? UpdateLevel(FrameNumber / every, levels) : NoLevel;
    GiState[0].extra.y = valid;
    GiState[0].levels = levels;
    GiState[0].resolution = passParams.resolution;
    GiState[0].updateLevel = level;
    float3 shift = float3(0.0, 0.0, 0.0);
    [loop] for (uint i = 0u; i < levels; i++)
    {
        float voxel = passParams.voxelSize * pow((float)passParams.levelScale, (float)i);
        float3 origin = LevelOrigin(CameraPos, passParams.resolution, voxel);
        bool wasValid = (valid & (1u << i)) != 0u;
        [branch] if (i == level && wasValid)
            shift = round((origin - GiState[0].origins[i].xyz) / voxel);
        [branch] if (i == level || !wasValid)
            GiState[0].origins[i] = float4(origin, voxel);
    }

    [branch] if (level != NoLevel)
    {
        uint flipped = 1u;
        [flatten] if (level == 0u) parity.x ^= flipped;
        else if (level == 1u) parity.y ^= flipped;
        else if (level == 2u) parity.z ^= flipped;
        else parity.w ^= flipped;
        valid |= 1u << level;
    }

    GiState[0].shift = float4(shift, 0.0);
    GiState[0].parity = parity;
    GiState[0].valid = valid;
}
