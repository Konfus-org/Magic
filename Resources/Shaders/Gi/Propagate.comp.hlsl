// The light in the level being rebuilt, one thread per voxel, written into the other set of spherical harmonics
// by the way it travels (a surface gathers what travels against its normal, Include/Gi.hlsli): what the voxel's
// occupied neighbours shine into it (their face towards it lit by the sun through its cascades and by the lights of
// its grid cell through their shadow maps, times their albedo over pi as a matte face reflects it, plus what they
// emit), and a damped share of what its empty neighbours held last time (read where they were, since the level may
// have moved). Light never crosses an occupied voxel, so a wall a voxel thick stops it. Every frame a level is
// rebuilt it takes one more bounce from the frame before, settling on a fixed point: a geometric tail of bounces,
// with no history beyond the two sets of harmonics, each level's current light in the set its parity names (the
// plan pass flips it each time the level is rebuilt), and nothing stochastic.

#include "Include/Frame.hlsli"
#include "Include/Math.hlsli"
#include "Gi/Common.hlsli"
#include "Lighting/Common.hlsli"

Texture3D<float4> Albedo : READ(0);
SamplerState AlbedoSampler : SAMPLER(0);
Texture3D<float4> Emissive : READ(1);
SamplerState EmissiveSampler : SAMPLER(1);
Texture2D<float> ShadowAtlas : READ(2);
SamplerComparisonState ShadowAtlasSampler : SAMPLER(2);
StructuredBuffer<GpuLight> Lights : READ(3);
StructuredBuffer<uint> LightGrid : READ(4);
StructuredBuffer<GpuShadowView> ShadowViews : READ(5);
StructuredBuffer<GpuShadowHeader> ShadowHeader : READ(6);
StructuredBuffer<GpuGiState> GiState : READ(7);

// The light volumes, both sets stacked (Gi/State.hlsli): the level's old light is loaded from the set its parity
// left, the new written into the other. RW for both, since SDL forbids sampling a texture bound for write.
[[vk::image_format("rgba16f")]]
RWTexture3D<float4> ShR : WRITE(0);
[[vk::image_format("rgba16f")]]
RWTexture3D<float4> ShG : WRITE(1);
[[vk::image_format("rgba16f")]]
RWTexture3D<float4> ShB : WRITE(2);

struct PassParams
{
    float propagationDamping = 0.9;
    float shadowFilterRadius = 0.03; // how the voxels read the shadow maps: as the lighting's settings
    float shadowNormalBias = 1.5;
    float shadowDepthBias = 1.0;
};

#include "Gi/State.hlsli"
#define SHADOW_ONE_TAP 1 // a voxel face is far wider than any penumbra
#include "Lighting/Shadows.hlsli"

static const int3 Neighbours[6] = { int3(1, 0, 0), int3(-1, 0, 0), int3(0, 1, 0), int3(0, -1, 0), int3(0, 0, 1), int3(0, 0, -1) };

// Each neighbour's share of what it passes on, with the damping: six faces share the voxel's light.
static const float NeighbourShare = 1.0 / 6.0;

float4 VoxelAt(Texture3D<float4> volume, SamplerState volumeSampler, uint level, int3 voxel)
{
    return volume.SampleLevel(volumeSampler, GiStackedUv(level, (float3)voxel + 0.5), 0.0);
}

void WriteSh(uint level, uint parity, uint3 voxel, float4 r, float4 g, float4 b)
{
    int3 texel = GiShTexel(level, parity, voxel);
    ShR[texel] = r;
    ShG[texel] = g;
    ShB[texel] = b;
}

// The direct light on a point of a surface facing normal (camera relative, as the shadows want it).
float3 DirectLight(ShadowFilter filter, float3 positionRel, float3 normal, uint3 cell)
{
    float3 light = float3(0.0, 0.0, 0.0);
    float3 toSun = -SunDirection;
    float sunFacing = saturate(dot(normal, toSun));
    [branch] if (sunFacing > 0.0)
    {
        float visible = (ShadowHeader[0].flags & ShadowSunFlag) != 0u ? SunVisibilityAt(filter, positionRel, normal) : 1.0;
        light += SunColor * sunFacing * visible;
    }

    uint row = GiLightRow(cell);
    uint count = min(LightGrid[row], GI_LIGHTS_PER_CELL);
    [loop] for (uint i = 0u; i < count; i++)
    {
        GpuLight local = Lights[LightGrid[row + 1u + i]];
        float3 lightToSurface = positionRel - LightNearest(local, positionRel);
        float facing = saturate(dot(normal, NormalizeOrZero(-lightToSurface)));
        [branch] if (facing <= 0.0)
            continue;

        float shadow = 1.0;
        [branch] if ((ShadowHeader[0].flags & ShadowLocalFlag) != 0u && local.shadow.x != ShadowNone)
            shadow = LocalShadow(filter, local, positionRel, normal);
        light += Arriving(local, lightToSurface) * facing * shadow;
    }

    return light;
}

// What an occupied voxel holds: no light of its own, and none passes through it (an empty voxel only takes on what
// its empty neighbours held), but a surface's filtered read reaches into the voxel it fills, so it keeps the
// average of what its empty neighbours held last time rather than black.
void WriteOccupied(uint level, int3 voxel, float3 shift, uint parity, bool oldValid)
{
    float4 r = float4(0.0, 0.0, 0.0, 0.0), g = r, b = r;
    float count = 0.0;
    int res = (int)GiResolution();
    uint oldParity = parity ^ 1u;
    [branch] if (oldValid)
    {
        [unroll] for (uint n = 0u; n < 6u; n++)
        {
            int3 other = voxel + Neighbours[n];
            int3 old = other + (int3)shift;
            if (any(other < 0) || any(other >= res) || any(old < 0) || any(old >= res))
                continue;
            if (VoxelAt(Albedo, AlbedoSampler, level, other).a >= GiOccupied)
                continue;

            int3 oldTexel = GiShTexel(level, oldParity, (uint3)old);
            r += ShR[oldTexel];
            g += ShG[oldTexel];
            b += ShB[oldTexel];
            count += 1.0;
        }
    }

    float share = count > 0.0 ? 1.0 / count : 0.0;
    WriteSh(level, parity, (uint3)voxel, r * share, g * share, b * share);
}

[numthreads(4, 4, 4)]
void main(uint3 threadId : SV_DispatchThreadID)
{
    if (!GiRebuilding() || any(threadId >= GiResolution()))
        return;

    uint level = GiUpdateLevel();
    int3 voxel = (int3)threadId;
    int res = (int)GiResolution();
    uint parity = GiParity(level);
    uint oldParity = parity ^ 1u;
    float4 here = VoxelAt(Albedo, AlbedoSampler, level, voxel);
    float3 shift = GiState[0].shift.xyz;
    bool oldValid = GiLevelWasValid(level); // the other set holds this level once it was built
    [branch] if (here.a >= GiOccupied)
    {
        WriteOccupied(level, voxel, shift, parity, oldValid);
        return;
    }

    float voxelSize = GiVoxel(level);
    float3 origin = GiOrigin(level);
    uint3 cell = (uint3)clamp(voxel * (int)GI_LIGHT_CELLS / res, 0, (int)GI_LIGHT_CELLS - 1);
    float4 shR = float4(0.0, 0.0, 0.0, 0.0), shG = shR, shB = shR;
    PassParams passParams = LoadPassParams();
    float damping = passParams.propagationDamping;
    ShadowFilter filter = { 0.0, passParams.shadowFilterRadius, passParams.shadowNormalBias, passParams.shadowDepthBias };
    [unroll] for (uint n = 0u; n < 6u; n++)
    {
        int3 other = voxel + Neighbours[n];
        if (any(other < 0) || any(other >= res))
            continue;

        float3 toHere = -(float3)Neighbours[n];
        float4 neighbour = VoxelAt(Albedo, AlbedoSampler, level, other);
        [branch] if (neighbour.a >= GiOccupied)
        {
            // An occupied neighbour: its face towards this voxel, lit.
            float3 facePoint = origin + ((float3)other + 0.5 + toHere * 0.5) * voxelSize;
            float3 direct = DirectLight(filter, facePoint - CameraPos, toHere, cell);
            float3 emissive = VoxelAt(Emissive, EmissiveSampler, level, other).rgb;
            float3 radiance = direct * neighbour.rgb / Pi + emissive;
            float4 lobe = ShLobe(toHere);
            shR += lobe * radiance.r;
            shG += lobe * radiance.g;
            shB += lobe * radiance.b;
        }
        else if (oldValid)
        {
            // An empty neighbour: what it held last time, where it was then, passed on this way.
            int3 old = other + (int3)shift;
            if (any(old < 0) || any(old >= res))
                continue;

            int3 oldTexel = GiShTexel(level, oldParity, (uint3)old);
            float4 lobe = ShLobe(toHere) * (damping * NeighbourShare);
            shR += lobe * ShIrradiance(ShR[oldTexel], toHere);
            shG += lobe * ShIrradiance(ShG[oldTexel], toHere);
            shB += lobe * ShIrradiance(ShB[oldTexel], toHere);
        }
    }

    WriteSh(level, parity, threadId, shR, shG, shB);
}
