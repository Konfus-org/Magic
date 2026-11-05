// The translucent clusters drawn after the lighting, forward, one indirect draw a cluster from the translucent
// draws the cull left, over the opaque depth: first their absorbance and the moments of their depths, added up;
// then each fragment's light, the sun through its pages and the glass, the lights of its froxel and the screen
// probes' reflection, weighed by how much reaches it through the panes in front, added up with how much it bends
// the picture behind; and, into the sun's glass shadow, their tints multiplied. The material the entries are
// specialised to says what each surface is made of. The pass header's x is the frame's constants, y the shadow
// constants, z the lighting constants and w the field constants.
module "DrawTranslucent";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Core.Pack;
import Core.Sampling;
import Scene.Instances;
import Scene.Clusters;
import Scene.Vertices;
import Scene.Visibility;
import Scene.Materials;
import Scene.Lights;
import Scene.Lighting;
import Scene.Probes;
import Scene.Translucency;
import Surface.Surface;
import Surface.Brdf;
import Material.IMaterial;
import Light.Lights;
import Shadow.Virtual;
import Sky.ISky;

// How far, in the output, a pane bends what is behind it per unit of its normal across the view.
static const float RefractionStrength = 0.08;

struct Varyings
{
    float4 position : SV_Position;
    float3 relative : RELATIVE;
    float3 normal : NORMAL;
    float3 tangent : TANGENT;
    float2 uv : TEXCOORD0;
    nointerpolation uint instance : INSTANCE;
    nointerpolation uint material : MATERIAL;
    nointerpolation float tangentSign : TANGENT_SIGN;
};

Varyings MakeVaryings(FrameConstants frame, uint vertexIndex, uint instanceIndex)
{
    ArrayRef<VisibleCluster> visible = { frame.visibleClusters };
    VisibleCluster entry = visible[instanceIndex];
    GpuInstance instance = LoadInstance(frame.instances, entry.instance);
    GpuCluster cluster = LoadCluster(frame.clusters, entry.cluster);
    Vertex vertex = LoadVertex(frame.vertices, LoadIndex(frame.indices, cluster.indexOffset + vertexIndex));
    Varyings output;
    output.relative = Relative(frame, ToWorld(instance.world, vertex.position));
    output.normal = ToWorldNormal(instance.world, vertex.normal);
    output.tangent = ToWorldDirection(instance.world, vertex.tangent);
    output.tangentSign = vertex.tangentSign;
    output.uv = vertex.uv;
    output.instance = entry.instance;
    output.material = GetMaterial(instance, cluster.materialSlot);
    output.position = mul(float4(output.relative, 1.0), frame.viewProjection);
    return output;
}

[shader("vertex")]
Varyings DrawTranslucentVertex(uint vertexIndex : SV_VertexID, uint instanceIndex : SV_StartInstanceLocation)
{
    return MakeVaryings(GetFrame(), vertexIndex, instanceIndex);
}

// The surface the material makes of a fragment.
SurfaceOutputs EvaluateSurface<M : IMaterial>(FrameConstants frame, Varyings input)
{
    SurfaceInputs inputs;
    inputs.position = input.relative;
    inputs.normal = normalize(input.normal);
    inputs.tangent = NormalizeSafe(input.tangent, float3(1.0, 0.0, 0.0));
    inputs.tangentSign = input.tangentSign;
    inputs.uv = input.uv;
    inputs.uvDdx = ddx(input.uv);
    inputs.uvDdy = ddy(input.uv);
    inputs.viewDirection = NormalizeSafe(-input.relative, float3(0.0, 0.0, -1.0));
    inputs.material = LoadMaterial(frame.materials, input.material);
    inputs.sampler = inputs.material.baseColorSampler != 0u ? inputs.material.baseColorSampler : frame.defaultSampler;
    return M.Evaluate(inputs);
}

// How much of each colour a pane holds back, from its colour and how much it holds back in all.
float3 GetTint(SurfaceOutputs surface) { return 1.0 - surface.alpha * (1.0 - surface.baseColor); }

float GetViewDepth(FrameConstants frame, float3 relative) { return max(mul(float4(relative, 1.0), frame.view).z, frame.near); }

struct MomentOutputs
{
    float4 optical : SV_Target0;
    float4 moments : SV_Target1;
};

// Each fragment's absorbance, by colour and in all, and the power moments of its warped depth.
[shader("pixel")]
MomentOutputs TranslucentMomentsPixel<M : IMaterial>(Varyings input)
{
    FrameConstants frame = GetFrame();
    TranslucencyConstants translucency = GetTranslucency();
    SurfaceOutputs surface = EvaluateSurface<M>(frame, input);
    float alpha = saturate(surface.alpha);
    float absorbance = -log(max(1.0 - alpha, 1e-4));
    float3 absorbanceRgb = -log(max(GetTint(surface), 1e-4));
    float z = WarpDepth(translucency, GetViewDepth(frame, input.relative));
    MomentOutputs output;
    output.optical = float4(absorbanceRgb, absorbance);
    output.moments = absorbance * float4(z, z * z, z * z * z, z * z * z * z);
    return output;
}

struct ResolveOutputs
{
    float4 accumulation : SV_Target0;
    float4 offsets : SV_Target1;
};

// Each fragment's light and how it bends the picture, weighed by what reaches it through the panes in front.
[shader("pixel")]
ResolveOutputs TranslucentResolvePixel<M : IMaterial, S : ISky>(Varyings input)
{
    FrameConstants frame = GetFrame();
    LightingConstants lighting = GetLighting();
    TranslucencyConstants translucency = GetTranslucency();
    GiConstants gi = LoadConstants<GiConstants>(lighting.gi);
    SurfaceOutputs surface = EvaluateSurface<M>(frame, input);
    float alpha = saturate(surface.alpha);
    uint2 pixel = uint2(input.position.xy);
    float viewDepth = GetViewDepth(frame, input.relative);
    float z = WarpDepth(translucency, viewDepth);

    TextureRef opticalTexture = { translucency.opticalDepth };
    TextureRef momentsTexture = { translucency.moments };
    float b0 = opticalTexture.Load(pixel, 0u).w;
    float4 moments = momentsTexture.Load(pixel, 0u) / max(b0, Epsilon);
    float through = b0 > Epsilon ? TransmittanceFromMoments(b0, moments, z) : 1.0;

    float3 normal = normalize(surface.normal);
    float3 viewDirection = NormalizeSafe(-input.relative, float3(0.0, 0.0, -1.0));
    if (dot(normal, viewDirection) < 0.0)
        normal = -normal;

    float3 world = input.relative + frame.cameraPosition.xyz;
    Brdf brdf = MakeBrdf(surface.baseColor, surface.metalness, surface.roughness, 0.5);

    SkyInputs sky;
    sky.viewDirection = reflect(-viewDirection, normal);
    sky.sunDirection = frame.sunDirection.xyz;
    sky.hasSunDisc = true;
    sky.sunIlluminance = lighting.sunIlluminance.rgb;
    sky.altitude = lighting.cameraAltitude.x;
    sky.transmittanceLut = lighting.transmittanceLut;
    sky.multiScatteringLut = lighting.multiScatteringLut;
    sky.skyViewLut = lighting.skyViewLut;
    sky.sampler = lighting.lutSampler;
    bool hasSky = lighting.hasSky != 0u;

    ShadowConstants shadow = LoadShadowConstants(GetPassHeader().y);
    float3 sunIlluminance = hasSky ? S.GetSunIlluminance(sky) : lighting.sunIlluminance.rgb;
    float3 sunVisible = SampleSunShadow(shadow, world, normal, viewDepth) * GetGlassShadow(translucency, world);
    float3 color = Evaluate(brdf, normal, viewDirection, frame.sunDirection.xyz) * sunIlluminance * sunVisible;

    uint froxel = GetFroxelIndex(GetFroxel(frame, pixel, viewDepth));
    uint count = GetFroxelLightCount(lighting.froxelLists, froxel);
    for (uint i = 0u; i < count; i++)
    {
        GpuLight light = LoadLight(lighting.lights, GetFroxelLight(lighting.froxelLists, froxel, i));
        LightSample sample = SampleLight(light, world, normal);
        color += Evaluate(brdf, normal, viewDirection, sample.direction) * sample.irradiance;
    }

    // The reflection of the world: the screen probes' radiance towards it, the sky when none lies on the pane.
    float3 irradiance;
    float3 reflected;
    if (!SampleScreenProbesAt(gi, pixel, world, normal, viewDepth, sky.viewDirection, irradiance, reflected))
        reflected = hasSky ? S.Evaluate(sky) : frame.skyRadiance.rgb * lighting.sunIlluminance.r;

    float nDotV = max(dot(normal, viewDirection), 1e-4);
    float3 fresnel = brdf.f0 + (max(float3(1.0 - brdf.alpha), brdf.f0) - brdf.f0) * Pow5(1.0 - nDotV);
    color += fresnel * reflected + surface.emissive;

    // The bend: along the normal's slant across the view, more the more the pane holds back.
    float3 viewNormal = mul(float4(normal, 0.0), frame.view).xyz;
    float2 offset = -viewNormal.xy * float2(1.0, -1.0) * (RefractionStrength * alpha);
    float weight = alpha * through;
    ResolveOutputs output;
    output.accumulation = float4(color * weight, weight);
    output.offsets = float4(offset * weight, 0.0, 0.0);
    return output;
}

// The four screen probes around a pixel on a pane's plane, as the lighting pass reads them for a surface.
bool SampleScreenProbesAt(GiConstants gi, uint2 pixel, float3 world, float3 normal, float viewDepth, float3 direction, out float3 irradiance, out float3 reflected)
{
    irradiance = float3(0.0);
    reflected = float3(0.0);
    float2 tileSpace = ((float2(pixel) + 0.5) / float(ScreenProbeTile)) - 0.5;
    float2 floored = floor(tileSpace);
    float2 fraction = tileSpace - floored;
    int2 base = int2(floored);
    int2 last = int2(gi.screenProbesX, gi.screenProbesY) - 1;
    TextureRef filtered = { gi.screenFiltered };
    ArrayRef<float4> irradianceTable = { gi.screenIrradiance };
    float weightSum = 0.0;
    for (int y = 0; y < 2; y++)
    {
        for (int x = 0; x < 2; x++)
        {
            uint2 tile = uint2(clamp(base + int2(x, y), int2(0), last));
            uint index = GetScreenProbeIndex(gi, tile);
            ScreenProbe probe = LoadScreenProbe(gi.screenProbes, index);
            if ((probe.flags & ScreenProbeFlagValid) == 0u)
                continue;

            float bilinear = (x == 0 ? 1.0 - fraction.x : fraction.x) * (y == 0 ? 1.0 - fraction.y : fraction.y);
            // A pane's probes sit on the surface behind it: only their normal must agree, not their plane.
            float weight = bilinear * saturate((dot(normal, probe.normal) - 0.5) / 0.5);
            if (weight <= 0.0)
                continue;

            irradiance += irradianceTable[index].rgb * weight;
            reflected += SampleScreenProbe(filtered, GetScreenProbeOrigin(tile), GetScreenProbeTexel(CreateBasis(probe.normal), direction)) * weight;
            weightSum += weight;
        }
    }

    if (weightSum < 1e-3)
        return false;

    irradiance /= weightSum;
    reflected /= weightSum;
    return true;
}

struct ShadowVaryings
{
    float4 position : SV_Position;
    float3 normal : NORMAL;
    float3 tangent : TANGENT;
    float2 uv : TEXCOORD0;
    nointerpolation uint material : MATERIAL;
    nointerpolation float tangentSign : TANGENT_SIGN;
};

// Each translucent cluster into the sun's glass shadow, by the sun's orthographic view.
[shader("vertex")]
ShadowVaryings GlassShadowVertex(uint vertexIndex : SV_VertexID, uint instanceIndex : SV_StartInstanceLocation)
{
    FrameConstants frame = GetFrame();
    TranslucencyConstants translucency = GetTranslucency();
    ArrayRef<VisibleCluster> visible = { frame.visibleClusters };
    VisibleCluster entry = visible[instanceIndex];
    GpuInstance instance = LoadInstance(frame.instances, entry.instance);
    GpuCluster cluster = LoadCluster(frame.clusters, entry.cluster);
    Vertex vertex = LoadVertex(frame.vertices, LoadIndex(frame.indices, cluster.indexOffset + vertexIndex));
    ShadowVaryings output;
    output.position = mul(float4(ToWorld(instance.world, vertex.position), 1.0), translucency.sunViewProjection);
    output.normal = ToWorldNormal(instance.world, vertex.normal);
    output.tangent = ToWorldDirection(instance.world, vertex.tangent);
    output.tangentSign = vertex.tangentSign;
    output.uv = vertex.uv;
    output.material = GetMaterial(instance, cluster.materialSlot);
    return output;
}

// The tint the pane leaves on the sun, multiplied into the glass shadow.
[shader("pixel")]
float4 GlassShadowPixel<M : IMaterial>(ShadowVaryings input) : SV_Target0
{
    FrameConstants frame = GetFrame();
    Varyings full;
    full.position = input.position;
    full.relative = float3(0.0);
    full.normal = input.normal;
    full.tangent = input.tangent;
    full.uv = input.uv;
    full.instance = 0u;
    full.material = input.material;
    full.tangentSign = input.tangentSign;
    SurfaceOutputs surface = EvaluateSurface<M>(frame, full);
    return float4(GetTint(surface), 1.0);
}
