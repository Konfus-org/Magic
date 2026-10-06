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

#include "Include/Frame.hlsli"
#include "Include/DebugText.hlsli"
#include "Include/Failure.hlsli"
#include "Include/GBuffer.hlsli"
#include "Lighting/Ambient.hlsli"
#include "Lighting/Common.hlsli"
#include "Lighting/Shading.hlsli"

#define GROUP_SIZE 8 // pixels a side per group

struct PassParams
{
    float aoStrength = 1.0;          // how much of the occlusion darkens the ambient
    bool aoMultiBounce = true;       // let the ambient bounce in creases (Jimenez 2016)
    float shadowBlend = 0.15;        // share of a cascade's range it blends into the next over
    float shadowFilterRadius = 0.03; // the penumbra's radius in metres
    float shadowNormalBias = 1.5;    // shadow texels a receiver is moved along its normal
    float shadowDepthBias = 1.0;     // shadow texels a receiver is moved towards the light
    float giIntensity = 0.85;        // multiplies the bounced light
    Color giInteriorTint = Color(0.03, 0.035, 0.05, 1.0); // what an interior is lit with where the sky cannot reach
    float giInteriorStrength = 1.0;
    float giFadeVoxels = 4.0;        // voxels from a GI level's edge over which it blends into the next
};

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
Texture2D<float4> Ao : READ(5);
SamplerState AoSampler : SAMPLER(5);
Texture2D<float> ShadowAtlas : READ(6);
SamplerComparisonState ShadowAtlasSampler : SAMPLER(6);
Texture3D<float4> GiShR : READ(7);
SamplerState GiShRSampler : SAMPLER(7);
Texture3D<float4> GiShG : READ(8);
SamplerState GiShGSampler : SAMPLER(8);
Texture3D<float4> GiShB : READ(9);
SamplerState GiShBSampler : SAMPLER(9);
Texture3D<float> GiSkyVis : READ(10);
SamplerState GiSkyVisSampler : SAMPLER(10);
Texture3D<float4> GiAlbedo : READ(11);
SamplerState GiAlbedoSampler : SAMPLER(11);
StructuredBuffer<GpuLight> Lights : READ(12);
StructuredBuffer<uint> Tiles : READ(13);
StructuredBuffer<uint> Clusters : READ(14);
StructuredBuffer<GpuShadowView> ShadowViews : READ(15);
StructuredBuffer<GpuShadowHeader> ShadowHeader : READ(16);
StructuredBuffer<GpuGiState> GiState : READ(17);

[[vk::image_format("rgba16f")]]
RWTexture2D<float4> Hdr : WRITE(0);

#include "Lighting/Shadows.hlsli"
#include "Include/Gi.hlsli"

// The cascades' colours in the sun shadow debug view: red, green, blue, yellow, the far view's magenta, and white
// where nothing shadows the point.
static const float3 CascadeTints[ShadowFarRow + 2] = { float3(1.0, 0.2, 0.2), float3(0.2, 1.0, 0.2), float3(0.3, 0.4, 1.0), float3(1.0, 1.0, 0.2), float3(1.0, 0.4, 1.0), float3(1.0, 1.0, 1.0) };

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

    PassParams passParams = LoadPassParams();
    float3 color = surface.emissive;
    [branch] if (depth > 0.0)
    {
        // Relative to the camera. An orthographic view looks along its axis from everywhere.
        float viewDepth = ViewDepth(depth);
        float3 position = ViewToWorld(ViewPosition(float2(viewPixel) + 0.5, viewDepth));
        float3 toCamera = IsOrthographic != 0u ? ViewToWorld(float3(0.0, 0.0, -1.0)) : NormalizeOrZero(-position);

        // The ambient, darkened where the screen-space occlusion found the surface closed in, let bounce so a bright
        // surface fills its own creases, and gathered along the bent normal (where the open sky is) once there is a GI
        // volume to gather from.
        float4 aoTexel = Ao.SampleLevel(AoSampler, uv, 0.0);
        float visibility = lerp(1.0, aoTexel.w, passParams.aoStrength);
        float3 bentNormal = NormalizeOrZero(aoTexel.xyz * 2.0 - 1.0);
        float3 diffuseColor = surface.baseColor * (1.0 - surface.metallic);
        float3 ambientOcclusion = passParams.aoMultiBounce ? MultiBounceAo(visibility, diffuseColor) : visibility.xxx;

        // The ambient: the sky where the volume says it is seen, the interior tint where not, and the bounced light,
        // all gathered along the bent normal; the flat sky colour where no volume holds the pixel or the GI is off.
        float3 ambient = Ambient;
        float skyVisibility = 1.0;
        [branch] if (GiLevels() != 0u)
        {
            GiSample gi = GiAt(position, bentNormal, passParams.giFadeVoxels);
            float3 interior = passParams.giInteriorTint.rgb * passParams.giInteriorStrength;
            float3 volumeAmbient = Ambient * gi.skyVisibility + interior * (1.0 - gi.skyVisibility) + gi.irradiance * passParams.giIntensity;
            ambient = lerp(Ambient, volumeAmbient, gi.weight);
            skyVisibility = lerp(1.0, gi.skyVisibility, gi.weight);
        }

        color += diffuseColor * ambient * surface.occlusion * ambientOcclusion;

        // The sun, through its cascades.
        float3 toSun = -SunDirection;
        uint shadowFlags = ShadowHeader[0].flags;
        ShadowFilter filter = { passParams.shadowBlend, passParams.shadowFilterRadius, passParams.shadowNormalBias, passParams.shadowDepthBias };
        uint cascade = ShadowFarRow + 1u; // none, until the sun's shadow says
        float sunVisible = 1.0;
        [branch] if ((shadowFlags & ShadowSunFlag) != 0u)
            sunVisible = SunShadow(filter, position, surface.normal, viewDepth, saturate(dot(surface.normal, toSun)), cascade);
        color += SunColor * sunVisible * Reflected(surface, toSun, toCamera);

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
            float3 lightToSurface = position - LightNearest(light, position);
            float3 toHighlight = NormalizeOrZero(LightAlong(light, position, reflect(-toCamera, surface.normal)) - position);
            float shadow = 1.0;
            [branch] if ((shadowFlags & ShadowLocalFlag) != 0u && light.shadow.x != ShadowNone)
                shadow = LocalShadow(filter, light, position, surface.normal);
            color += Arriving(light, lightToSurface) * shadow * Reflected(surface, NormalizeOrZero(-lightToSurface), toHighlight, toCamera);
        }

        // The debug views (DeferredRenderer's DebugView, in the frame's flags): one part of the picture instead of it.
        uint debugView = FrameDebugView();
        [branch] if (debugView == DebugViewAlbedo)
            color = surface.baseColor;
        else if (debugView == DebugViewNormals)
            color = surface.normal * 0.5 + 0.5;
        else if (debugView == DebugViewRoughness)
            color = surface.roughness.xxx;
        else if (debugView == DebugViewMetallic)
            color = surface.metallic.xxx;
        else if (debugView == DebugViewEmissive || debugView == DebugViewLevelOfDetail)
            color = surface.emissive;
        else if (debugView == DebugViewDepth)
            color = (1.0 - saturate(log2(max(viewDepth, 1.0)) / 8.0)).xxx; // a metre white, 256 m black, evenly per doubling
        else if (debugView == DebugViewSunShadow)
            color = CascadeTints[min(cascade, ShadowFarRow + 1u)] * lerp(0.15, 1.0, sunVisible);
        else if (debugView == DebugViewAmbientOcclusion)
            color = visibility.xxx;
        else if (debugView == DebugViewBentNormals)
            color = bentNormal * 0.5 + 0.5;
        else if (debugView == DebugViewLightCount)
        {
            // Blue for none, through green, to red for a full cluster.
            float share = saturate((float)reaching / (float)LightsPerCluster);
            color = reaching == 0u ? float3(0.05, 0.05, 0.3) : lerp(lerp(float3(0.1, 0.3, 1.0), float3(0.1, 1.0, 0.2), saturate(share * 2.0)), float3(1.0, 0.1, 0.1), saturate(share * 2.0 - 1.0));
        }
        else if (debugView == DebugViewGiLight)
            color = ambient;
        else if (debugView == DebugViewSkyVisibility)
            color = skyVisibility.xxx;
        else if (debugView == DebugViewVoxelAlbedo || debugView == DebugViewVoxelCoverage)
        {
            // The voxel the surface itself fills: half a voxel in from the pixel, in the finest level that holds it.
            float4 voxelValue = float4(0.0, 0.0, 0.0, 0.0);
            [loop] for (uint level = 0u; level < GiLevels(); level++)
            {
                if (!GiLevelValid(level))
                    continue;

                float3 voxel = (position + (CameraPos - GiOrigin(level)) - surface.normal * 0.5 * GiVoxel(level)) / GiVoxel(level);
                if (all(voxel >= 0.0) && all(voxel < (float)GiResolution()))
                {
                    voxelValue = GiAlbedo.SampleLevel(GiAlbedoSampler, GiStackedUv(level, voxel), 0.0);
                    break;
                }
            }

            color = debugView == DebugViewVoxelAlbedo ? voxelValue.rgb : voxelValue.aaa;
        }

        [branch] if (reaching > LightsPerCluster)
        {
            float3 overflowGlow = FailureColor * FailureGlow * FailurePulse(Time);
            color = OverflowTextPixel(viewPixel, reaching) ? OverflowTextColor : lerp(color, overflowGlow, OverflowGlowShare);
        }
    }

    Hdr[pixel] = float4(color, 1.0);
}
