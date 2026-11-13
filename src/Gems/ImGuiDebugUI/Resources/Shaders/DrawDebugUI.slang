// The debug UI over a finished picture: 2D triangles in target pixels, +Y down from the top left corner, each vertex
// with its texture coordinate and an RGBA8 colour, read from the UI's vertices by index, then the draw's texture
// (ImGui's atlas of characters, with a white texel for solid shapes) times the vertex colour, blended over the target
// by its alpha. ImGui's colours are already encoded, as a target holding encoded values wants them. The pass header's
// x is the constants, y the draw's first vertex, which its indices count from, and z its texture.
module "DrawDebugUI";

import Core.Heap;
import Core.Pass;
import Core.Pack;

// One of the UI's vertices, as ImGui writes it.
struct DebugUIVertex
{
    float2 position;
    float2 uv;
    uint color;
};

// C#'s DebugUIConstants.
struct DebugUIConstants
{
    // One over the target's size in pixels.
    float2 inverseSize;
    uint vertices;
    uint sampler;
};

struct Varyings
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
    float4 color : TEXCOORD1;
};

[shader("vertex")]
Varyings DrawDebugUIVertex(uint index : SV_VertexID)
{
    DebugUIConstants constants = LoadConstants<DebugUIConstants>();
    ArrayRef<DebugUIVertex> vertices = { constants.vertices };
    DebugUIVertex vertex = vertices[GetPassHeader().y + index];
    float2 ndc = vertex.position * constants.inverseSize * 2.0 - 1.0;

    Varyings output;
    output.position = float4(ndc.x, -ndc.y, 0.0, 1.0);
    output.uv = vertex.uv;
    output.color = UnpackUnorm4x8(vertex.color);
    return output;
}

[shader("pixel")]
float4 DrawDebugUIPixel(Varyings input) : SV_Target0
{
    DebugUIConstants constants = LoadConstants<DebugUIConstants>();
    TextureRef texture = { GetPassHeader().z };
    SamplerRef sampler = { constants.sampler };
    float4 color = texture.SampleLevel(sampler, input.uv, 0.0) * input.color;
    return float4(color.rgb * color.a, color.a);
}
