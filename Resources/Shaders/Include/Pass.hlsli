// What a data pass shader (a .pass asset's .frag.hlsl or .comp.hlsl) is written against. The renderer
// composes it in front of the shader, after defining PASS_OUTPUT_FORMAT (the vk::image_format of a compute
// pass's output: rgba16f, rgba8, r32f, ...), and follows it with one Texture2D + SamplerState per input,
// named after the input (Hdr and HdrSampler), and the generated LoadPassParams() that unpacks the pass's
// parameters from the words appended to the frame block.
//
// A pass shader declares `struct PassParams { ... }` (same rules as MaterialParams, no textures) and, for a
// fragment pass, `float4 main(PassVaryings input) : SV_Target0`; for a compute pass, `main` with
// SV_DispatchThreadID writing Output[threadId.xy]. The frame fields (ViewSize, Time, ...) are all here too.
// Inputs are read with SampleLevel(..., 0.0): a pass maps texels one to one, and the same code then works
// in a compute pass, where Sample does not exist.

#ifndef MAGIC_PASS_HLSLI
#define MAGIC_PASS_HLSLI

// The pass's parameters, packed by the CPU; LoadPassParams() reads them.
#define FRAME_APPEND uint4 PassRaw[8];
#include "Include/Frame.hlsli"

// What Passes/Fullscreen.vert.hlsl hands a fragment pass. uv has the texture convention: (0, 0) top left.
struct PassVaryings
{
    float2 uv : TEXCOORD0;
    float4 position : SV_Position;
};

#ifdef STAGE_COMPUTE
[[vk::image_format(PASS_OUTPUT_FORMAT)]]
RWTexture2D<float4> Output : WRITE(0);
#endif

#endif
