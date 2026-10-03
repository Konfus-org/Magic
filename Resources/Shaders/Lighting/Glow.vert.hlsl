// A glow: an unlit bright dot, what a light source looks like from too far away to see anything it lights.
// One camera-facing square per row of the glows buffer, six vertices each, built from the vertex id alone
// (no vertex buffer); Glow.frag.hlsl cuts the disk out of it and writes it into the gbuffer as pure
// emission, so it costs a few pixels and no lighting. It is never smaller than GlowMinPixels on screen:
// a far light stays a dot instead of flickering in and out between pixels. One that would be smaller is
// dimmed instead, by how much smaller, down to GlowMinBrightness: a field of far lights thins out into
// faint points rather than running together.
//
// VsOut here and PsIn in Glow.frag.hlsl are one struct written twice: keep them the same, member for member.

#include "Include/Frame.hlsli"
#include "Include/Structs.hlsli"

// The radius, in pixels, a glow is at least. Bigger keeps far glows easier to see but lets a field of them
// run together; under one pixel they shimmer as the camera moves.
static const float GlowMinPixels = 1.0;

// The least of its colour a glow keeps, however much smaller than GlowMinPixels it would be. Higher keeps
// the farthest lights plain to see; lower lets them fade into the distance.
static const float GlowMinBrightness = 0.35;

// The corner of the square each of the six vertices is: two triangles, (0, 1, 2) and (2, 1, 3).
static const uint CornerOfVertex[6] = { 0u, 1u, 2u, 2u, 1u, 3u };

StructuredBuffer<GpuGlow> Glows : READ(0);

struct VsOut
{
    float2 corner : TEXCOORD0; // -1..1 across the square
    float3 color : TEXCOORD1;
    float4 position : SV_Position;
};

VsOut main(uint vertexId : SV_VertexID)
{
    GpuGlow glow = Glows[vertexId / 6u];
    uint cornerIndex = CornerOfVertex[vertexId % 6u];
    float2 corner = float2((cornerIndex & 1u) != 0u ? 1.0 : -1.0, (cornerIndex & 2u) != 0u ? 1.0 : -1.0);

    float3 fromCamera = glow.positionRadius.xyz - CameraPos;
    float viewDepth = max(mul(View, float4(fromCamera, 0.0)).z, Near);

    // The view-space radius that is GlowMinPixels on screen there; an orthographic view's does not grow with depth.
    float spread = IsOrthographic != 0u ? 1.0 : viewDepth;
    float minRadius = GlowMinPixels * spread / (ProjScale.y * ViewSize.y * 0.5);
    float radius = max(glow.positionRadius.w, minRadius);
    float brightness = clamp(glow.positionRadius.w / minRadius, GlowMinBrightness, 1.0);

    // A view-space offset from the centre is the same offset times the projection's diagonal in clip space.
    VsOut output;
    output.position = mul(ViewProj, float4(fromCamera, 1.0));
    output.position.xy += corner * radius * ProjScale;
    output.corner = corner;
    output.color = glow.color.rgb * brightness;
    return output;
}
