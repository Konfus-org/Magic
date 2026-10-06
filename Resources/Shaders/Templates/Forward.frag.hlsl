// The forward template: the renderer composes it after a transparent material's surface shader (SurfaceVariant
// Transparent, SURFACE_FORWARD 1) and draws it in the pipeline's transparency stage, depth tested against the opaque
// depth and not written. It lights the surface as Lighting/Lighting.comp.hlsl lights the gbuffer
// (Lighting/Shading.hlsli): the sky and the GI along the normal, the sun through its cascades, and the lights of the
// pixel's transparency tile (Lighting/LightCull.comp.hlsl with TRANSPARENT_TILES: every light that reaches anything
// in front of the opaque scene there) through their shadow maps.
//
// Nothing is sorted. Each layer adds its colour, weighted by its opacity and its depth, to the transparency targets,
// and builds up their coverage (weighted blended order-independent transparency, McGuire and Bavoil 2013);
// Transparency/Composite.comp.hlsl then puts the weighted average over the scene by the coverage. Overlapping layers
// of any colour come out in any order the same way, the nearer ones counting for more.
//
// The bindings are the pools and then, in this order, what Resources/Passes/Core/Transparent*.pass reads: the shadow
// atlas, the three light volumes and the sky visibility of the GI, the scene behind (Include/Surface.hlsli), the
// nearest refracting depth; the materials; the lights, the transparency tiles, the shadow views and header, and the
// GI state.
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
Texture3D<float4> GiShR : READ(9);
SamplerState GiShRSampler : SAMPLER(9);
Texture3D<float4> GiShG : READ(10);
SamplerState GiShGSampler : SAMPLER(10);
Texture3D<float4> GiShB : READ(11);
SamplerState GiShBSampler : SAMPLER(11);
Texture3D<float> GiSkyVis : READ(12);
SamplerState GiSkyVisSampler : SAMPLER(12);
// The depth of the nearest refracting surface (reverse-Z, 0 for none), for the Behind and Nearest layers; a draw that
// writes it (Depth) binds a scratch texture here instead.
Texture2D<float> TransparentFront : READ(14);
SamplerState TransparentFrontSampler : SAMPLER(14);
StructuredBuffer<GpuLight> Lights : READ(16);
StructuredBuffer<uint> Tiles : READ(17);
StructuredBuffer<GpuShadowView> ShadowViews : READ(18);
StructuredBuffer<GpuShadowHeader> ShadowHeader : READ(19);
StructuredBuffer<GpuGiState> GiState : READ(20);

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
            + GiShR.SampleLevel(GiShRSampler, at, 0.0).rgb + GiShG.SampleLevel(GiShGSampler, at, 0.0).rgb
            + GiShB.SampleLevel(GiShBSampler, at, 0.0).rgb
            + GiSkyVis.SampleLevel(GiSkyVisSampler, at, 0.0).xxx
            + SceneColor.SampleLevel(SceneColorSampler, input.uv, 0.0).rgb + TransparentFront.SampleLevel(TransparentFrontSampler, input.uv, 0.0).xxx
            + Lights[input.material].colorInnerCos.rgb + asfloat(Tiles[input.material]).xxx
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

// What a layer adds to the transparency targets: its colour times its opacity, and its opacity, both weighted by how
// near it is, into the first; its opacity, as coverage, into the second; its depth, max-blended into the nearest
// depth, into the third (only when the pass asks for the depth: 0 adds nothing).
struct TransparentLayer
{
    float4 weighted : SV_Target0;
    float coverage : SV_Target1;
    float front : SV_Target2;
};

// Which layers the pass draws (TransparentLayers, the executor's row): all of them; for refracting glass, which shows
// the scene behind it, first only the nearest depth, then the surfaces behind it over the scene with the other
// transparents in, then the nearest over all of that.
static const uint LayersAll = 0u;
static const uint LayersDepth = 1u;
static const uint LayersBehind = 2u;
static const uint LayersNearest = 3u;

uint ForwardLayers()
{
    return PassRaw[7].w;
}

// Whether the fragment is the nearest refracting surface at its pixel: its depth is the nearest one written there.
bool IsNearest(float4 position)
{
    float width;
    float height;
    TransparentFront.GetDimensions(width, height);
    float nearest = TransparentFront.SampleLevel(TransparentFrontSampler, position.xy / float2(width, height), 0.0);
    return position.z >= nearest - max(nearest * 1e-5, 1e-7);
}

// Whether a light's share is enough to show, so its shadow is worth looking up (linear HDR, before exposure).
bool Visible(float3 lit)
{
    return max(lit.r, max(lit.g, lit.b)) > 1e-3;
}

// A layer's weight: near layers count for more than far ones, so the nearer of two overlapping layers shows more
// (McGuire and Bavoil's depth weight, capped low enough that bright HDR layers do not overflow half floats).
float LayerWeight(float alpha, float viewDepth)
{
    float distance = abs(viewDepth);
    return alpha * clamp(10.0 / (1e-5 + pow(distance / 5.0, 2.0) + pow(distance / 200.0, 6.0)), 1e-2, 3e2);
}

TransparentLayer main(MeshVaryings input, bool isFrontFace : SV_IsFrontFace)
{
    SurfaceInputs surfaceInputs = SurfaceInputsOf(input, isFrontFace);
    MaterialParams material = LoadMaterialParams(input.material);
    Surface surface = EvaluateSurface(surfaceInputs, material);
    clip(LodCoverage(input.lodFade, input.position.xy));
    clip(FrameDebugView() == DebugViewNormal ? 1.0 : -1.0); // a debug view shows the opaque scene's parts alone

    uint layers = ForwardLayers();
    [branch] if (layers == LayersDepth)
    {
        TransparentLayer depthOnly;
        depthOnly.weighted = float4(0.0, 0.0, 0.0, 0.0);
        depthOnly.coverage = 0.0;
        depthOnly.front = input.position.z;
        return depthOnly;
    }

    [branch] if (layers == LayersBehind || layers == LayersNearest)
        clip(IsNearest(input.position) == (layers == LayersNearest) ? 1.0 : -1.0);

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

    // The sun, through its cascades. A light's shadow is only looked up where the light would add something: glass
    // has no diffuse, so it only matters inside the small highlights, and most of its pixels skip the filter's taps.
    ShadowFilter filter = { ForwardShadowBlend, ForwardShadowRadius, ForwardNormalBias, ForwardDepthBias };
    uint shadowFlags = ShadowHeader[0].flags;
    float3 toSun = -SunDirection;
    uint cascade = ShadowMaxCascades;
    float3 sunLit = SunColor * Reflected(shaded, toSun, toCamera);
    [branch] if ((shadowFlags & ShadowSunFlag) != 0u && Visible(sunLit))
        sunLit *= SunShadow(filter, position, shaded.normal, viewDepth, saturate(dot(shaded.normal, toSun)), cascade);
    color += sunLit;

    // The lights of the pixel's transparency tile. Their range window leaves out those that do not reach this point.
    uint2 viewSize = (uint2)ViewSize;
    uint2 viewPixel = min((uint2)max(input.position.xy - ViewOrigin, 0.0), viewSize - 1u);
    uint2 tile = viewPixel / LightTileSize;
    uint tilesAcross = (viewSize.x + LightTileSize - 1u) / LightTileSize;
    uint row = (tile.y * tilesAcross + tile.x) * LightTileWords;
    uint count = min(Tiles[row], LightsPerTile);
    [loop] for (uint index = 0u; index < count; index++)
    {
        GpuLight light = Lights[Tiles[row + LightTileHeader + index]];
        float3 lightToSurface = position - LightNearest(light, position);
        float3 toHighlight = NormalizeOrZero(LightAlong(light, position, reflect(-toCamera, shaded.normal)) - position);
        float3 lit = Arriving(light, lightToSurface) * Reflected(shaded, NormalizeOrZero(-lightToSurface), toHighlight, toCamera);
        [branch] if ((shadowFlags & ShadowLocalFlag) != 0u && light.shadow.x != ShadowNone && Visible(lit))
            lit *= LocalShadow(filter, light, position, shaded.normal);
        color += lit;
    }

    float alpha = saturate(surface.alpha);
    float weight = LayerWeight(alpha, viewDepth);
    TransparentLayer layer;
    layer.weighted = float4(color * alpha, alpha) * weight;
    layer.coverage = alpha;
    layer.front = 0.0;
    return layer;
}
