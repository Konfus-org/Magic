// The impostor cards' shadow pass, fragment stage: a card casts only where its frame's baked coverage is (the frame
// picked between the two either side of the light's direction by an ordered dither). The texture pools are bound
// as a material pass binds them (Include/Surface.hlsli), so the atlases are read the same way.

#include "Include/Surface.hlsli"
#include "Include/Impostor.hlsli"

struct PsIn
{
    float4 position : SV_Position;
    float4 clip : SV_ClipDistance0;
    float2 frameUv : TEXCOORD0;
    nointerpolation uint4 impostor : TEXCOORD1;
};

void main(PsIn input)
{
    uint frame = ImpostorFrame(input.impostor.z, asfloat(input.impostor.w), DitherValue(input.position.xy));
    float2 at = ImpostorAtlasUv(frame, input.frameUv);
    float coverage = SampleTextureGrad(input.impostor.x, at, float2(0.0, 0.0), float2(0.0, 0.0)).b;
    clip(coverage - 0.5);
}
