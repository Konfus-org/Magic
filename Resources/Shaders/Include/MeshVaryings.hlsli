// What the mesh vertex shader hands the fragment stage: declared once so the two stages cannot disagree. On
// the DXIL path the fragment signature must line up with the vertex one register for register, or the
// pipeline fails to link.

#ifndef MAGIC_MESH_VARYINGS_HLSLI
#define MAGIC_MESH_VARYINGS_HLSLI

struct MeshVaryings
{
    float3 worldPosition : TEXCOORD0;
    float3 normal : TEXCOORD1;
    float4 tangent : TEXCOORD2; // w = bitangent sign
    float2 uv : TEXCOORD3;
    nointerpolation uint material : TEXCOORD4; // slot in the material table
    nointerpolation uint flags : TEXCOORD5;    // GpuInstance.flags
    nointerpolation float lodFade : TEXCOORD6; // GpuVisible.lodFade
    // An impostor's card (Include/Impostor.hlsli): its atlases' references, the frames it shows and how much of the
    // second (asuint); all zero for a mesh. A card's normal and tangent carry the model's X and Y axes in the world,
    // and its uv the point of the frame.
    nointerpolation uint4 impostor : TEXCOORD7;
    float4 position : SV_Position;
};

#endif
