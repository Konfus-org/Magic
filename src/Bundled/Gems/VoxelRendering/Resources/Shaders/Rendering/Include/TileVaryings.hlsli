// What the resolve's vertex stage (Rendering/Materials/Tiles.vert.hlsl) hands its fragment stage: the resolve bin the
// tile was given, and where the pixel is.

#ifndef MAGIC_RENDERING_TILE_VARYINGS_HLSLI
#define MAGIC_RENDERING_TILE_VARYINGS_HLSLI

struct TileVaryings
{
    nointerpolation uint bin : TEXCOORD0;
    float4 clip : SV_Position;
};

#endif
