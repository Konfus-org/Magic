// The forward template: the renderer composes it after a transparent material's surface shader (SurfaceVariant
// Transparent, SURFACE_FORWARD 1) and draws it in the pipeline's transparency stage, blended over the lit scene in
// Hdr, depth tested against the opaque depth and not written. It lights the surface as Lighting/Lighting.comp.hlsl
// lights the gbuffer (Lighting/Shading.hlsli): the sky and the GI along the normal, the sun through its cascades,
// and the lights of the pixel's cluster through their shadow maps. The bindings are the pools and then, in this
// order, what Resources/Passes/Core/Transparent*.pass reads: the shadow atlas, the six light sets and the sky
// visibility of the GI; the materials; the lights, the view's tiles and clusters, the shadow views and header,
// and the GI state. Nothing is sorted: transparent instances blend in the order their buckets come, which is
// right for one layer and approximate for several.
//
// The lighting's tunables are the defaults of the lighting pass (a composed material pipeline has no parameter
// block of its own).

#include "Include/GBuffer.hlsli"
#include "Include/MeshFragment.hlsli"
#include "Include/MeshVaryings.hlsli"
#include "Include/Surface.hlsli"
#include "Lighting/Common.hlsli"
#include "Lighting/Shading.hlsli"

Texture2D<float> ShadowAtlas : READ(8);
SamplerComparisonState ShadowAtlasSampler : SAMPLER(8);
Texture3D<float4> GiShR0 : READ(9);
SamplerState GiShR0Sampler : SAMPLER(9);
Texture3D<float4> GiShR1 : READ(10);
SamplerState GiShR1Sampler : SAMPLER(10);
Texture3D<float4> GiShG0 : READ(11);
SamplerState GiShG0Sampler : SAMPLER(11);
Texture3D<float4> GiShG1 : READ(12);
SamplerState GiShG1Sampler : SAMPLER(12);
Texture3D<float4> GiShB0 : READ(13);
SamplerState GiShB0Sampler : SAMPLER(13);
Texture3D<float4> GiShB1 : READ(14);
SamplerState GiShB1Sampler : SAMPLER(14);
Texture3D<float> GiSkyVis : READ(15);
SamplerState GiSkyVisSampler : SAMPLER(15);
StructuredBuffer<GpuLight> Lights : READ(17);
StructuredBuffer<uint> Tiles : READ(18);
StructuredBuffer<uint> Clusters : READ(19);
StructuredBuffer<GpuShadowView> ShadowViews : READ(20);
StructuredBuffer<GpuShadowHeader> ShadowHeader : READ(21);
StructuredBuffer<GpuGiState> GiState : READ(22);

#include "Lighting/Shadows.hlsli"
#include "Include/Gi.hlsli"

// Keeps every lighting binding and the frame block alive whatever the surface is; returns zero. A surface that
// is black and rough (Unlit.surf, the failure surface) zeroes every light term, DXC folds the lighting away with
// the resources it read, and the reflected counts then no longer place the buffers where they are declared (D3D12
// refuses the pipeline). Like KeepBindingsAlive: the condition never holds, and nothing folds away.
float3 KeepLightingAlive(MeshVaryings input)
{
    float3 nothing = float3(0.0, 0.0, 0.0);
    [branch] if (input.flags == 0xFFFFFFFFu)
    {
        float3 at = float3(input.uv, 0.5);
        nothing = ShadowAtlas.SampleCmpLevelZero(ShadowAtlasSampler, input.uv, 0.5).xxx
            + GiShR0.SampleLevel(GiShR0Sampler, at, 0.0).rgb + GiShR1.SampleLevel(GiShR1Sampler, at, 0.0).rgb
            + GiShG0.SampleLevel(GiShG0Sampler, at, 0.0).rgb + GiShG1.SampleLevel(GiShG1Sampler, at, 0.0).rgb
            + GiShB0.SampleLevel(GiShB0Sampler, at, 0.0).rgb + GiShB1.SampleLevel(GiShB1Sampler, at, 0.0).rgb
            + GiSkyVis.SampleLevel(GiSkyVisSampler, at, 0.0).xxx
            + Lights[input.material].colorInnerCos.rgb + asfloat(Tiles[input.material]).xxx + asfloat(Clusters[input.material]).xxx
            + ShadowViews[input.material].eye.xyz + asfloat(ShadowHeader[0].flags).xxx + GiState[0].shift.xyz
            + CameraPos + Ambient;
    }

    return nothing;
}

// The lighting pass's defaults (Resources/Passes/Core/Lighting.pass).
static const float ForwardShadowBlend = 0.15;
static const float ForwardShadowRadius = 0.03;
static const float ForwardNormalBias = 1.5;
static const float ForwardDepthBias = 1.0;
static const float ForwardGiFadeVoxels = 4.0;
static const float ForwardGiIntensity = 0.85;
static const float3 ForwardInteriorTint = float3(0.03, 0.035, 0.05);

float4 main(MeshVaryings input, bool isFrontFace : SV_IsFrontFace) : SV_Target0
{
    SurfaceInputs surfaceInputs = SurfaceInputsOf(input, isFrontFace);
    MaterialParams material = LoadMaterialParams(input.material);
    Surface surface = EvaluateSurface(surfaceInputs, material);
    clip(LodCoverage(input.lodFade, input.position.xy));

    GBufferSurface shaded;
    shaded.emissive = surface.emissive;
    shaded.baseColor = surface.baseColor;
    shaded.normal = NormalizeOrZero(surface.normal);
    shaded.roughness = surface.roughness;
    shaded.metallic = surface.metallic;
    shaded.occlusion = surface.occlusion;

    // Relative to the camera, as the lighting works.
    float3 position = input.worldPosition - CameraPos;
    float3 toCamera = IsOrthographic != 0u ? ViewToWorld(float3(0.0, 0.0, -1.0)) : NormalizeOrZero(-position);
    float viewDepth = mul(View, float4(position, 0.0)).z;
    float3 color = shaded.emissive + KeepBindingsAlive(input) + KeepLightingAlive(input);

    // The ambient: the sky and the bounce of the GI where a level holds the point, the flat sky colour elsewhere.
    float3 diffuseColor = shaded.baseColor * (1.0 - shaded.metallic);
    float3 ambient = Ambient;
    [branch] if (GiLevels() != 0u)
    {
        GiSample gi = GiAt(position, shaded.normal, ForwardGiFadeVoxels);
        float3 volumeAmbient = Ambient * gi.skyVisibility + ForwardInteriorTint * (1.0 - gi.skyVisibility) + gi.irradiance * ForwardGiIntensity;
        ambient = lerp(Ambient, volumeAmbient, gi.weight);
    }

    color += diffuseColor * ambient * shaded.occlusion;

    // The sun, through its cascades.
    ShadowFilter filter = { ForwardShadowBlend, ForwardShadowRadius, ForwardNormalBias, ForwardDepthBias };
    uint shadowFlags = ShadowHeader[0].flags;
    float3 toSun = -SunDirection;
    uint cascade = ShadowMaxCascades;
    float sunVisible = 1.0;
    [branch] if ((shadowFlags & ShadowSunFlag) != 0u)
        sunVisible = SunShadow(filter, position, shaded.normal, viewDepth, saturate(dot(shaded.normal, toSun)), cascade);
    color += SunColor * sunVisible * Reflected(shaded, toSun, toCamera);

    // The lights of the pixel's cluster; a tile nothing opaque was drawn in has none.
    uint2 viewSize = (uint2)ViewSize;
    uint2 viewPixel = min((uint2)max(input.position.xy - ViewOrigin, 0.0), viewSize - 1u);
    uint2 tile = viewPixel / LightTileSize;
    uint tilesAcross = (viewSize.x + LightTileSize - 1u) / LightTileSize;
    uint tileIndex = tile.y * tilesAcross + tile.x;
    float tileNear = asfloat(Tiles[tileIndex * LightTileWords + 1u]);
    float tileFar = asfloat(Tiles[tileIndex * LightTileWords + 2u]);
    [branch] if (tileNear > 0.0 && tileFar >= tileNear)
    {
        uint cluster = tileIndex * LightSlices + LightSlice(clamp(viewDepth, tileNear, tileFar), tileNear, tileFar);
        uint row = cluster * LightClusterWords;
        uint count = min(Clusters[row], LightsPerCluster);
        [loop] for (uint index = 0u; index < count; index++)
        {
            GpuLight light = Lights[Clusters[row + 1u + index]];
            float3 lightToSurface = position - (light.positionRange.xyz - CameraPos);
            float shadow = 1.0;
            [branch] if ((shadowFlags & ShadowLocalFlag) != 0u && light.shadow.x != ShadowNone)
                shadow = LocalShadow(filter, light, position, shaded.normal);
            color += Arriving(light, lightToSurface) * shadow * Reflected(shaded, NormalizeOrZero(-lightToSurface), toCamera);
        }
    }

    return float4(color, saturate(surface.alpha));
}
