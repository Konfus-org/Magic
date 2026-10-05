// What a post shader (a .post asset's .frag.hlsl or .comp.hlsl) is written against, and the varyings every
// fullscreen pass is drawn with. For a post the renderer composes this in front of the shader, after defining
// PASS_OUTPUT_FORMAT (the vk::image_format of a compute post's output: rgba16f, rgba8, r32f, ...), and
// follows it with one Texture2D + SamplerState per input, named after the input (Hdr and HdrSampler), and the
// generated LoadPassParams() that unpacks the post's parameters from the frame block's PassRaw words.
//
// A post shader declares `struct PassParams { ... }` (same rules as MaterialParams, no textures) and, for a
// fragment post, `float4 main(PassVaryings input) : SV_Target0`; for a compute post, `main` with
// SV_DispatchThreadID writing Output[threadId.xy]. The frame fields (ViewSize, Time, ...) are all here too.
// Inputs are read with SampleLevel(..., 0.0): a post maps texels one to one, and the same code then works
// in a compute post, where Sample does not exist. A core pass (.pass) declares its own bindings and includes
// this only for PassVaryings when it is a fullscreen one.

#ifndef MAGIC_PASS_HLSLI
#define MAGIC_PASS_HLSLI

#include "Include/Frame.hlsli"

// What Passes/Fullscreen.vert.hlsl hands a fragment pass. uv has the texture convention: (0, 0) top left.
struct PassVaryings
{
    float2 uv : TEXCOORD0;
    float4 position : SV_Position;
};

#if defined(STAGE_COMPUTE) && defined(PASS_OUTPUT_FORMAT)
[[vk::image_format(PASS_OUTPUT_FORMAT)]]
RWTexture2D<float4> Output : WRITE(0);
#endif

#endif
