// How this renderer reads the textures a surface or a sky names (Core's Include/Surface.hlsli): a TextureRef is the
// texture's heap id, read through the view's material sampler.

#ifndef MAGIC_RENDERING_TEXTURES_HLSLI
#define MAGIC_RENDERING_TEXTURES_HLSLI

#include "Include/Surface.hlsli"
#include "Rendering/Include/View.hlsli"

float4 SampleTextureGrad(TextureRef texture, float2 uv, float2 uvDdx, float2 uvDdy)
{
    // A texture still loading is none: the surface's factor stands alone, as it does when it checks HasTexture.
    [branch] if (!HasTexture(texture))
    {
        return float4(1.0, 1.0, 1.0, 1.0);
    }

    // A GPU may fetch the texture's descriptor before it knows the branch above is not taken, and none is no place in
    // the heap: the index is kept inside it.
    uint index = min(texture, HEAP_TEXTURES - 1u);
    return TEXTURE(index).SampleGrad(SAMPLER_STATE(View.materialSampler), uv, uvDdx, uvDdy);
}

#endif
