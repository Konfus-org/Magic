// The material table and the eight texture pool slots, fragment stage. The pools are fixed Texture2DArrays
// (Bindings.hlsli POOL_*), all eight always bound so the reflected sampler count is the same for every
// material pass. A packed reference is (slot << 16) | layer; the slot is chosen with a constant-index
// switch because shadercross flattens resource arrays and a runtime index would not survive the trip.
// SampleSrgb is for colour data (base colour, emissive: slots 0..3), SampleLinear for data (normal, ORM:
// slots 4..7); each covers exactly the four slots of its format.
#ifndef MAGIC_MATERIAL_HLSLI
#define MAGIC_MATERIAL_HLSLI

#include "Include/Bindings.hlsli"
#include "Include/Structs.hlsli"

Texture2DArray PoolSrgb256 : PS_TEX(0);
SamplerState SampSrgb256 : PS_SAMP(0);
Texture2DArray PoolSrgb512 : PS_TEX(1);
SamplerState SampSrgb512 : PS_SAMP(1);
Texture2DArray PoolSrgb1024 : PS_TEX(2);
SamplerState SampSrgb1024 : PS_SAMP(2);
Texture2DArray PoolSrgb2048 : PS_TEX(3);
SamplerState SampSrgb2048 : PS_SAMP(3);
Texture2DArray PoolLinear256 : PS_TEX(4);
SamplerState SampLinear256 : PS_SAMP(4);
Texture2DArray PoolLinear512 : PS_TEX(5);
SamplerState SampLinear512 : PS_SAMP(5);
Texture2DArray PoolLinear1024 : PS_TEX(6);
SamplerState SampLinear1024 : PS_SAMP(6);
Texture2DArray PoolLinear2048 : PS_TEX(7);
SamplerState SampLinear2048 : PS_SAMP(7);

// Storage buffers follow the sampled textures in SDL's binding order: slot 0 of the fragment storage
// buffers is t8 once eight sampled textures are declared.
StructuredBuffer<GpuMaterial> Materials : PS_BUF(8);

uint PoolSlot(uint packed)
{
    return packed >> 16;
}

uint PoolLayer(uint packed)
{
    return packed & 0xFFFF;
}

float4 SampleSrgb(uint packed, float2 uv)
{
    float3 c = float3(uv, PoolLayer(packed));
    [branch] switch (PoolSlot(packed))
    {
        case POOL_SRGB_256: return PoolSrgb256.Sample(SampSrgb256, c);
        case POOL_SRGB_512: return PoolSrgb512.Sample(SampSrgb512, c);
        case POOL_SRGB_1024: return PoolSrgb1024.Sample(SampSrgb1024, c);
        default: return PoolSrgb2048.Sample(SampSrgb2048, c);
    }
}

float4 SampleLinear(uint packed, float2 uv)
{
    float3 c = float3(uv, PoolLayer(packed));
    [branch] switch (PoolSlot(packed))
    {
        case POOL_LINEAR_256: return PoolLinear256.Sample(SampLinear256, c);
        case POOL_LINEAR_512: return PoolLinear512.Sample(SampLinear512, c);
        case POOL_LINEAR_1024: return PoolLinear1024.Sample(SampLinear1024, c);
        default: return PoolLinear2048.Sample(SampLinear2048, c);
    }
}

// The pools share one repeat sampler per slot; a material that wants clamping says so with a flag.
float2 MaterialUv(GpuMaterial material, float2 uv)
{
    uint flags = asuint(material.roughMetalNormalFlags.w);
    return (flags & MATERIAL_CLAMP_UV) != 0 ? saturate(uv) : uv;
}

#endif
