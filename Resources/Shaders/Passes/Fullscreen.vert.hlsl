// One triangle covering the screen, from the vertex id alone; the fragment stage of every fullscreen data
// pass runs behind it.

#include "Include/Pass.hlsli"

PassVaryings main(uint vertexId : SV_VertexID)
{
    // (0, 0), (2, 0), (0, 2): the corner the triangle covers the screen from, in uv.
    float2 uv = float2((float)((vertexId << 1u) & 2u), (float)(vertexId & 2u));

    PassVaryings output;
    output.uv = uv;
    output.position = float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, 0.0, 1.0);
    return output;
}
