// The varyings of the fullscreen triangle (Passes/Fullscreen.vert.hlsl): what the fragment stage of a fullscreen
// pass takes. uv has the texture convention: (0, 0) top left.

#ifndef MAGIC_FULLSCREEN_HLSLI
#define MAGIC_FULLSCREEN_HLSLI

struct PassVaryings
{
    float2 uv : TEXCOORD0;
    float4 position : SV_Position;
};

#endif
