// The light in the level being rebuilt, one thread per voxel, written into the other set of spherical harmonics:
// what the voxel's occupied neighbours shine into it (their face towards it lit by the sun through its cascades,
// by the lights of its grid cell through their shadow maps, and by what they emit, times their albedo), plus a
// damped share of what its empty neighbours held last time (read where they were, since the level may have moved).
// Light never crosses an occupied voxel, so a wall a voxel thick stops it. Every frame a level is rebuilt it
// takes one more bounce from the frame before, settling on a fixed point: a geometric tail of bounces, with no
// history buffer beyond the one set of harmonics and nothing stochastic.

#include "Include/Shade.hlsli"
#include "Gi/Common.hlsli"
#include "Lighting/Common.hlsli"

Texture3D<float4> Albedo : READ(0);
SamplerState AlbedoSampler : SAMPLER(0);
Texture3D<float4> Emissive : READ(1);
SamplerState EmissiveSampler : SAMPLER(1);
Texture3D<float4> OldR : READ(2);
SamplerState OldRSampler : SAMPLER(2);
Texture3D<float4> OldG : READ(3);
SamplerState OldGSampler : SAMPLER(3);
Texture3D<float4> OldB : READ(4);
SamplerState OldBSampler : SAMPLER(4);
Texture2D<float> ShadowAtlas : READ(5);
SamplerComparisonState ShadowAtlasSampler : SAMPLER(5);
StructuredBuffer<GpuLight> Lights : READ(6);
StructuredBuffer<uint> LightGrid : READ(7);
StructuredBuffer<GpuShadowRecord> ShadowRecords : READ(8);

[[vk::image_format("rgba16f")]]
RWTexture3D<float4> NewR : WRITE(0);
[[vk::image_format("rgba16f")]]
RWTexture3D<float4> NewG : WRITE(1);
[[vk::image_format("rgba16f")]]
RWTexture3D<float4> NewB : WRITE(2);

#include "Lighting/Shadows.hlsli"

static const int3 Neighbours[6] = { int3(1, 0, 0), int3(-1, 0, 0), int3(0, 1, 0), int3(0, -1, 0), int3(0, 0, 1), int3(0, 0, -1) };

// Each neighbour's share of what it passes on, with the damping: six faces share the voxel's light.
static const float NeighbourShare = 1.0 / 6.0;

float4 VoxelAt(Texture3D<float4> volume, SamplerState volumeSampler, uint level, int3 voxel)
{
    return volume.SampleLevel(volumeSampler, GiStackedUv(level, (float3)voxel + 0.5), 0.0);
}

// The direct light on a point of a surface facing normal (camera relative, as the shadows want it).
float3 DirectLight(float3 positionRel, float3 normal, uint3 cell)
{
    float3 light = float3(0.0, 0.0, 0.0);
    float3 toSun = -SunDirection;
    float sunFacing = saturate(dot(normal, toSun));
    [branch] if (sunFacing > 0.0)
    {
        float visible = (ShadowFlags & ShadowSunFlag) != 0u ? SunVisibilityAt(positionRel, normal) : 1.0;
        light += SunColor * sunFacing * visible;
    }

    uint row = GiLightRow(cell);
    uint count = min(LightGrid[row], GI_LIGHTS_PER_CELL);
    [loop] for (uint i = 0u; i < count; i++)
    {
        GpuLight local = Lights[LightGrid[row + 1u + i]];
        float3 lightToSurface = positionRel - (local.positionRange.xyz - CameraPos);
        float facing = saturate(dot(normal, NormalizeOrZero(-lightToSurface)));
        [branch] if (facing <= 0.0)
            continue;

        float shadow = 1.0;
        [branch] if ((ShadowFlags & ShadowLocalFlag) != 0u && local.shadow.x != ShadowNone)
            shadow = LocalShadow(local, positionRel, normal);
        light += Arriving(local, lightToSurface) * facing * shadow;
    }

    return light;
}

[numthreads(4, 4, 4)]
void main(uint3 threadId : SV_DispatchThreadID)
{
    if (any(threadId >= GiResolution))
        return;

    uint level = GiUpdateLevel;
    int3 voxel = (int3)threadId;
    int res = (int)GiResolution;
    int3 texel = GiStackedTexel(level, threadId);
    float4 here = VoxelAt(Albedo, AlbedoSampler, level, voxel);
    [branch] if (here.a >= GiOccupied)
    {
        NewR[texel] = NewG[texel] = NewB[texel] = float4(0.0, 0.0, 0.0, 0.0);
        return;
    }

    float voxelSize = GiVoxel(level);
    float3 origin = GiOrigin(level);
    uint3 cell = (uint3)clamp(voxel * (int)GI_LIGHT_CELLS / res, 0, (int)GI_LIGHT_CELLS - 1);
    float4 shR = float4(0.0, 0.0, 0.0, 0.0), shG = shR, shB = shR;
    float damping = GiShift.w;
    bool oldValid = (GiFlags & 256u) != 0u && GiLevelValid(level); // the old set holds this level once it was built
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
            float3 direct = DirectLight(facePoint - CameraPos, toHere, cell);
            float3 emissive = VoxelAt(Emissive, EmissiveSampler, level, other).rgb;
            float3 radiance = direct * neighbour.rgb + emissive;
            float4 lobe = ShLobe(toHere);
            shR += lobe * radiance.r;
            shG += lobe * radiance.g;
            shB += lobe * radiance.b;
        }
        else if (oldValid)
        {
            // An empty neighbour: what it held last time, where it was then, passed on this way.
            int3 old = other + (int3)GiShift.xyz;
            if (any(old < 0) || any(old >= res))
                continue;

            float4 lobe = ShLobe(toHere) * (damping * NeighbourShare);
            shR += lobe * ShIrradiance(VoxelAt(OldR, OldRSampler, level, old), toHere);
            shG += lobe * ShIrradiance(VoxelAt(OldG, OldGSampler, level, old), toHere);
            shB += lobe * ShIrradiance(VoxelAt(OldB, OldBSampler, level, old), toHere);
        }
    }

    NewR[texel] = shR;
    NewG[texel] = shG;
    NewB[texel] = shB;
}
