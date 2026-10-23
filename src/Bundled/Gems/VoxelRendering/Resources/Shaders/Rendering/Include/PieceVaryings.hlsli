// What the pieces' vertex stage (Rendering/Materials/Pieces.vert.hlsl) hands a fragment stage, declared once so the
// two cannot disagree: the fragment's inputs must line up with the vertex's outputs location for location. Every
// varying is the triangle's corner's, so a value all three corners share, as the triangle's id, needs no provoking
// vertex.

#ifndef MAGIC_RENDERING_PIECE_VARYINGS_HLSLI
#define MAGIC_RENDERING_PIECE_VARYINGS_HLSLI

struct PieceVaryings
{
    float3 position : TEXCOORD0;               // relative to the camera
    float3 normal : TEXCOORD1;
    float4 tangent : TEXCOORD2;                // w = bitangent sign
    float2 uv : TEXCOORD3;
    nointerpolation uint material : TEXCOORD4; // the material's row
    nointerpolation uint flags : TEXCOORD5;    // the instance's
    nointerpolation float4 card : TEXCOORD6;   // a card's view (0..8), turn, mirrored (1), and 1; all 0 for a cluster
    nointerpolation float2 dissolve : TEXCOORD7; // what of it is kept as it dissolves (DissolveOf)
    nointerpolation float3 front : TEXCOORD8;  // a card's way to the camera along the ground (xz), and its half width
    nointerpolation uint2 atlases : TEXCOORD9; // a card's two atlases' texture refs
    nointerpolation uint id : TEXCOORD10;      // the visibility buffer's name for its triangle
#if defined(STAGE_FRAGMENT)
    noperspective centroid float4 clip : SV_Position; // as a fragment that writes its own depth must read it
#else
    float4 clip : SV_Position;
#endif
};

// What the bins that do not clip need of a triangle: where it is and its name.
struct PiecePlace
{
    nointerpolation uint id : TEXCOORD0;
    float4 clip : SV_Position;
};

#endif
