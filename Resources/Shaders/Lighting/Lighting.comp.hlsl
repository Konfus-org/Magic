// The lighting: the one place light is computed. One thread per pixel of the view reads the gbuffer there,
// works out where the pixel is from its depth, and writes the lit colour into Hdr: what the surface emits,
// plus the sun and a flat ambient, plus the point and spot lights binned into the pixel's cluster: its
// screen tile at its depth (LightCull.comp.hlsl, then LightCluster.comp.hlsl). Every pixel of the view is written, so nothing is blended and Hdr needs no clear: a pixel
// nothing was drawn in keeps the gbuffer's emissive, which is the clear colour there.
//
// A cluster more lights reach than it holds (LightsPerCluster) is lit by the ones it kept and glows the
// failure magenta over that, breathing: lights were left out there, and which ones differs from cluster to
// cluster, so it is shown as the failure it is rather than left to look like a rendering fault. It says so
// too, in text drawn here (Include/DebugText.hlsli): "TOO MANY LIGHTS" along the top of the overfull tiles,
// and under it in each how many lights reach the cluster. The one failure decided on the GPU: the CPU never sees
// how many lights reach a tile.
//
// Screen space, so it costs the same whatever was drawn and treats everything in view alike: a far chunk's
// stand-in is lit exactly as the chunk is. Shading is Lambert plus a Blinn-Phong highlight sized by
// roughness, in camera-relative world space so it holds up far from the origin.

#include "Include/DebugText.hlsli"
#include "Include/Failure.hlsli"
#include "Include/GBuffer.hlsli"
#include "Lighting/Common.hlsli"

#define GROUP_SIZE 8 // pixels a side per group

// The Blinn-Phong exponent of a mirror-smooth and of a fully rough surface, and the highlight's strength.
static const float ShininessSmooth = 256.0;
static const float ShininessRough = 4.0;
static const float SpecularStrength = 0.5;

// How much of an overfull tile's colour is the failure glow: enough to be unmistakable, with the lights it
// kept still showing through.
static const float OverflowGlowShare = 0.6;

// What an overfull tile says, over and over across the screen, and the colour it says it in: above 1, so
// it stays white after the tonemap.
#define OVERFLOW_MESSAGE_LENGTH 16
static const uint OverflowMessage[OVERFLOW_MESSAGE_LENGTH] = { GlyphLetterA + 19u, GlyphLetterA + 14u, GlyphLetterA + 14u, GlyphSpace, GlyphLetterA + 12u, GlyphLetterA + 0u, GlyphLetterA + 13u, GlyphLetterA + 24u, GlyphSpace, GlyphLetterA + 11u, GlyphLetterA + 8u, GlyphLetterA + 6u, GlyphLetterA + 7u, GlyphLetterA + 19u, GlyphLetterA + 18u, GlyphSpace };
static const float3 OverflowTextColor = float3(2.0, 2.0, 2.0);
static const uint OverflowCountDigits = 4u;

Texture2D Emissive : READ(0);
SamplerState EmissiveSampler : SAMPLER(0);
Texture2D Albedo : READ(1);
SamplerState AlbedoSampler : SAMPLER(1);
Texture2D Normal : READ(2);
SamplerState NormalSampler : SAMPLER(2);
Texture2D Material : READ(3);
SamplerState MaterialSampler : SAMPLER(3);
Texture2D<float> Depth : READ(4);
SamplerState DepthSampler : SAMPLER(4);
StructuredBuffer<GpuLight> Lights : READ(5);
StructuredBuffer<uint> Tiles : READ(6);
StructuredBuffer<uint> Clusters : READ(7);

[[vk::image_format("rgba16f")]]
RWTexture2D<float4> Hdr : WRITE(0);

// How much of a light's colour the surface sends to the camera, for light arriving from toLight: the
// diffuse and the highlight together, zero when the surface faces away.
float3 Reflected(GBufferSurface surface, float3 toLight, float3 toCamera)
{
    float3 halfway = NormalizeOrZero(toLight + toCamera);
    float facing = saturate(dot(surface.normal, toLight));

    float shininess = lerp(ShininessSmooth, ShininessRough, surface.roughness);
    float highlight = pow(saturate(dot(surface.normal, halfway)), shininess);
    float specular = highlight * (1.0 - surface.roughness) * SpecularStrength;
    float3 diffuseColor = surface.baseColor * (1.0 - surface.metallic);
    float3 specularColor = lerp(float3(1.0, 1.0, 1.0), surface.baseColor, surface.metallic);

    return (diffuseColor * surface.occlusion + specularColor * specular) * facing;
}

// A point or spot light's colour as it arrives along lightToSurface (not normalised): inverse-square, eased
// to nothing at the light's range so the edge of its reach is never a visible line, and for a spot faded
// from the inner cone out to the outer one; and faded out as its whole reach gets too small on screen to see.
float3 Arriving(GpuLight light, float3 lightToSurface)
{
    float distanceSquared = dot(lightToSurface, lightToSurface);
    float range = light.positionRange.w;
    float reach = distanceSquared / max(range * range, 1e-6);
    float window = saturate(1.0 - reach * reach);
    float falloff = window * window / (distanceSquared + 1.0);

    float cosAngle = dot(NormalizeOrZero(lightToSurface), light.directionOuterCos.xyz);
    float innerCos = light.colorInnerCos.w;
    float outerCos = light.directionOuterCos.w;
    float cone = saturate((cosAngle - outerCos) / max(innerCos - outerCos, 1e-4));

    return light.colorInnerCos.rgb * (falloff * cone * LightScreenFade(light));
}

// Whether the pixel is ink of an overfull cluster's text. The message runs along the top of the tiles in
// the view's own pixels, not each tile's, so its words carry on from one tile into the next; under it each
// tile holds the count of the lights that reach the cluster.
bool OverflowTextPixel(uint2 viewPixel, uint reaching)
{
    uint2 inTile = viewPixel % LightTileSize;
    bool isMessageRow = inTile.y < GlyphCell.y;

    uint glyph = OverflowMessage[(viewPixel.x / GlyphCell.x) % (uint)OVERFLOW_MESSAGE_LENGTH];
    bool isMessage = GlyphPixel(glyph, uint2(viewPixel.x % GlyphCell.x, inTile.y));

    // One texel under the message's cell and one in from the tile's edge.
    uint2 inCount = uint2(inTile.x - 1u, inTile.y - GlyphCell.y - 1u);
    bool isCount = inTile.x >= 1u && inTile.y > GlyphCell.y && SmallNumberPixel(min(reaching, 9999u), OverflowCountDigits, inCount);

    return isMessageRow ? isMessage : isCount;
}

[numthreads(GROUP_SIZE, GROUP_SIZE, 1)]
void main(uint3 threadId : SV_DispatchThreadID)
{
    uint2 viewSize = (uint2)ViewSize;
    uint2 viewPixel = threadId.xy;
    if (any(viewPixel >= viewSize))
        return;

    uint targetWidth;
    uint targetHeight;
    Depth.GetDimensions(targetWidth, targetHeight);
    uint2 pixel = (uint2)ViewOrigin + viewPixel;
    float2 uv = (float2(pixel) + 0.5) / float2(targetWidth, targetHeight);

    float depth = Depth.SampleLevel(DepthSampler, uv, 0.0);
    GBufferSurface surface = DecodeGBuffer(
        Emissive.SampleLevel(EmissiveSampler, uv, 0.0).rgb,
        Albedo.SampleLevel(AlbedoSampler, uv, 0.0).rgb,
        Normal.SampleLevel(NormalSampler, uv, 0.0).rgb,
        Material.SampleLevel(MaterialSampler, uv, 0.0).rgb);

    float3 color = surface.emissive;
    [branch] if (depth > 0.0)
    {
        // Relative to the camera. An orthographic view looks along its axis from everywhere.
        float viewDepth = ViewDepth(depth);
        float3 position = ViewToWorld(ViewPosition(float2(viewPixel) + 0.5, viewDepth));
        float3 toCamera = IsOrthographic != 0u ? ViewToWorld(float3(0.0, 0.0, -1.0)) : NormalizeOrZero(-position);

        float3 diffuseColor = surface.baseColor * (1.0 - surface.metallic);
        color += diffuseColor * Ambient * surface.occlusion;
        color += SunColor * Reflected(surface, -SunDirection, toCamera);

        uint2 tile = viewPixel / LightTileSize;
        uint tilesAcross = (viewSize.x + LightTileSize - 1u) / LightTileSize;
        uint tileIndex = tile.y * tilesAcross + tile.x;
        float tileNear = asfloat(Tiles[tileIndex * LightTileWords + 1u]);
        float tileFar = asfloat(Tiles[tileIndex * LightTileWords + 2u]);
        uint cluster = tileIndex * LightSlices + LightSlice(viewDepth, tileNear, tileFar);
        uint row = cluster * LightClusterWords;
        uint reaching = Clusters[row];
        uint count = min(reaching, LightsPerCluster);
        [loop] for (uint index = 0u; index < count; index++)
        {
            GpuLight light = Lights[Clusters[row + 1u + index]];
            float3 lightToSurface = position - (light.positionRange.xyz - CameraPos);
            color += Arriving(light, lightToSurface) * Reflected(surface, NormalizeOrZero(-lightToSurface), toCamera);
        }

        [branch] if (reaching > LightsPerCluster)
        {
            float3 overflowGlow = FailureColor * FailureGlow * FailurePulse(Time);
            color = OverflowTextPixel(viewPixel, reaching) ? OverflowTextColor : lerp(color, overflowGlow, OverflowGlowShare);
        }
    }

    Hdr[pixel] = float4(color, 1.0);
}
