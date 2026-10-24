// How this renderer draws a sky written against Core/Sky, and the sky's light it captures. It hands the sky the frame's
// clocks, sun, moon and weather (CreateSkyInputs), loads the world's sky from its material row, and reads it along a
// direction (EvaluateSky). A pass that draws the sky is generic over the sky's type, which the renderer names as it
// compiles the pass: the world's sky's struct, or for a world with none, FallbackSky, the renderer's own clear sky as
// bright as the sun makes it, with the sun's disc. The passes, all compute:
//
//   WriteDfgTable   the DFG table every shading reads at (n.v, roughness): Lighting/Brdf's IntegrateDfg, a thread a
//                   texel; written once, as it depends on nothing but its shader
//   CaptureSky      the sky into the first level of a cube, a face a slice of the dispatch, its discs and what it gives
//                   off left out, which their own light stands for and which is never captured
//   FilterSkyLight  from the captured sky and its levels, the reflections for each roughness, every level of a cube, a
//                   face of a level a slice of the dispatch: level m the captured sky convolved with GGX at roughness
//                   m / (levels - 1), the view taken to be the normal and the reflection (Karis 2013), each sample read
//                   from the captured sky's level as wide as its share of the lobe (Krivanek & Colbert 2008, "Real-time
//                   Shading with Filtered Importance Sampling"), so few samples are smooth and a small bright sun does
//                   not sparkle; level 0, a mirror's, is the sky as captured. The first group of the last slice
//                   projects the light from everywhere on the real SH basis as nine harmonics, each texel weighted by
//                   its solid angle (Sloan 2008, "Stupid Spherical Harmonics Tricks")
//   DrawSky         the sky over what nothing was drawn on in a view: its light along the pixel's direction with its
//                   discs, pre-exposed by the view's exposure, and what it gives off pre-exposed as it is; each pixel's
//                   dither moves each frame, for a sky that integrates by steps

module "Renderer/Sky";

import Core.Noise;
import Lighting.Brdf;
__exported import Renderer.World;

// The fallback sky's colours, and its luminance per lux of the sun: a clear sky's zenith is around 0.03 to 0.08 of the
// sun's illuminance, in nits.
static const float3 FallbackZenith = float3(0.25, 0.45, 0.95);
static const float3 FallbackHorizon = float3(0.75, 0.85, 1.0);
static const float3 FallbackGround = float3(0.11, 0.105, 0.1);
static const float3 FallbackGlow = float3(1.0, 0.85, 0.6);
static const float FallbackNitsPerLux = 0.06;

// Nits, when there is no sun to scale it.
static const float FallbackNightSky = 0.002;

// Samples the DFG table integrates each texel by.
static const uint DfgSamples = 256u;

// Texels a side of the level the harmonics read, and threads in their group.
static const uint ShFaceSize = 16u;
static const uint ShThreads = 64u;

struct DfgConstants
{
    TextureRef dfg;          // written
}

struct CaptureConstants
{
    CubeRef cube;            // written, its first level, its faces as Core/Math's GetCubeDirection numbers them
}

struct FilterConstants
{
    CubeRef cube;            // the captured sky, with its levels
    SamplerRef sampler;      // linear
    CubeRef specular;        // written, every level
    ArrayRef<float4> sh;     // written, rgb each coefficient
    uint levels;             // the specular's levels, roughness 0 to 1
    uint samples;            // of the lobe, for each texel
}

struct SkyConstants
{
    TextureRef depth;        // the view's depth, 0 where nothing was drawn
    TextureRef target;       // drawn over where nothing was
}

groupshared float3 ShSums[ShThreads][9];

public struct FallbackSky : ISky
{
    public SkyRadiance Evaluate(SkyInputs input)
    {
        float3 direction = input.direction;
        float3 towardSun = -input.celestial.sunDirection;
        float sunLux = GetLuminance(input.celestial.sunIlluminance);
        float day = saturate(towardSun.y * 4.0 + 0.2);
        float up = direction.y;
        float3 sky = up >= 0.0
            ? lerp(FallbackHorizon, FallbackZenith, sqrt(saturate(up)))
            : lerp(FallbackHorizon * 0.6, FallbackGround, saturate(-up * 4.0));
        sky += FallbackGlow * (0.6 * pow(saturate(dot(direction, towardSun)), 8.0));
        float radius = max(input.celestial.sunAngularRadius, 1e-4);
        float disc = dot(direction, towardSun) >= cos(radius) && towardSun.y > 0.0 ? 1.0 : 0.0;

        SkyRadiance light;
        light.nits = sky * max(sunLux * FallbackNitsPerLux * day, FallbackNightSky);
        light.discs = input.celestial.sunIlluminance * (disc / (Pi * Pow2(radius)));
        light.emissive = float3(0.0, 0.0, 0.0);
        return light;
    }
}

// The sky's inputs along a direction, with how it changes a pixel across and a pixel down; a capture is drawn coarser.
// dither is the pixel's (Core/Sky).
public SkyInputs CreateSkyInputs(float3 direction, float3 directionDdx, float3 directionDdy, bool isCapture, float dither)
{
    SkyInputs inputs;
    inputs.direction = direction;
    inputs.directionDdx = directionDdx;
    inputs.directionDdy = directionDdy;
    inputs.cameraPosition = View.cameraPosition;
    inputs.frame = World.clocks;
    inputs.celestial = World.celestial;
    inputs.weather = World.weather;
    inputs.isCapture = isCapture;
    inputs.dither = dither;
    inputs.sampler = World.materialSampler;
    return inputs;
}

// The world's sky, K, its values loaded from the sky's material row.
public SkyRadiance EvaluateSky<K : ISky>(SkyInputs inputs)
{
    K sky = World.LoadMaterial<K>(World.skyMaterial);
    return sky.Evaluate(inputs);
}

// The view's direction through a point on screen, in pixels from the top left.
float3 GetPixelDirection(float2 pixel)
{
    return normalize(View.ToWorld(View.ToViewPosition(pixel, 1.0)));
}

[shader("compute")]
[numthreads(8, 8, 1)]
void WriteDfgTable(uint3 thread : SV_DispatchThreadID, uniform PassHeader pass, uniform DfgConstants c)
{
    LevelRef dfg = c.dfg.GetLevel(0u);
    uint2 size = dfg.size;
    if (any(thread.xy >= size))
    {
        return;
    }

    float2 at = (float2(thread.xy) + 0.5) / float2(size);
    dfg[thread.xy] = float4(IntegrateDfg(at.x, at.y, DfgSamples), 1.0);
}

[shader("compute")]
[numthreads(8, 8, 1)]
void CaptureSky<K : ISky>(uint3 id : SV_DispatchThreadID, uniform PassHeader pass, uniform CaptureConstants c)
{
    BeginPass(pass);
    FacesRef faces = c.cube.GetLevel(0u);
    uint2 size = faces.size;
    if (any(id.xy >= size))
    {
        return;
    }

    float2 texel = 1.0 / float2(size);
    float2 uv = (float2(id.xy) + 0.5) * texel;
    float3 direction = GetCubeDirection(id.z, uv);
    float3 directionDdx = GetCubeDirection(id.z, uv + float2(texel.x, 0.0)) - direction;
    float3 directionDdy = GetCubeDirection(id.z, uv + float2(0.0, texel.y)) - direction;
    SkyRadiance light = EvaluateSky<K>(CreateSkyInputs(direction, directionDdx, directionDdy, true, 0.5));
    faces[id] = float4(light.nits, 1.0);
}

// Projects the captured sky on the first nine harmonics, a group's lanes sharing its texels, read at the level with
// ShFaceSize texels a side.
void ProjectSkySh(FilterConstants c, uint thread)
{
    uint3 size = c.cube.size;
    float level = clamp(log2((float)size.x / (float)ShFaceSize), 0.0, (float)size.z - 1.0);
    float3 sums[9];
    [unroll] for (uint i = 0u; i < 9u; i++)
    {
        sums[i] = float3(0.0, 0.0, 0.0);
    }

    for (uint texel = thread; texel < 6u * ShFaceSize * ShFaceSize; texel += ShThreads)
    {
        uint face = texel / (ShFaceSize * ShFaceSize);
        uint inFace = texel % (ShFaceSize * ShFaceSize);
        float2 uv = (float2(inFace % ShFaceSize, inFace / ShFaceSize) + 0.5) / (float)ShFaceSize;
        float2 st = uv * 2.0 - 1.0;
        float solidAngle = 4.0 / (Pow2((float)ShFaceSize) * pow(1.0 + dot(st, st), 1.5));
        float3 direction = GetCubeDirection(face, uv);
        float3 radiance = c.cube.SampleLevel(c.sampler, direction, level).rgb * solidAngle;
        sums[0] += radiance * 0.282095;
        sums[1] += radiance * (0.488603 * direction.y);
        sums[2] += radiance * (0.488603 * direction.z);
        sums[3] += radiance * (0.488603 * direction.x);
        sums[4] += radiance * (1.092548 * direction.x * direction.y);
        sums[5] += radiance * (1.092548 * direction.y * direction.z);
        sums[6] += radiance * (0.315392 * (3.0 * direction.z * direction.z - 1.0));
        sums[7] += radiance * (1.092548 * direction.x * direction.z);
        sums[8] += radiance * (0.546274 * (direction.x * direction.x - direction.y * direction.y));
    }

    [unroll] for (uint j = 0u; j < 9u; j++)
    {
        ShSums[thread][j] = sums[j];
    }

    GroupMemoryBarrierWithGroupSync();
    [branch] if (thread < 9u)
    {
        float3 total = float3(0.0, 0.0, 0.0);
        for (uint other = 0u; other < ShThreads; other++)
        {
            total += ShSums[other][thread];
        }

        c.sh[thread] = float4(total, 0.0);
    }
}

// One texel of a level of the reflections: the captured sky convolved with GGX at the level's roughness.
float3 PrefilterTexel(FilterConstants c, float3 normal, uint level)
{
    uint3 captured = c.cube.size;
    if (level == 0u)
    {
        return c.cube.SampleLevel(c.sampler, normal, 0.0).rgb;
    }

    float roughness = (float)level / (float)max(c.levels - 1u, 1u);
    float alpha = ToGgxAlpha(roughness);

    // The solid angle a texel of the captured sky's first level covers, about.
    float texelAngle = 4.0 * Pi / (6.0 * (float)captured.x * (float)captured.x);
    float3 sum = float3(0.0, 0.0, 0.0);
    float weights = 0.0;
    [loop] for (uint i = 0u; i < c.samples; i++)
    {
        float3 halfway = ToWorldFromTangent(SampleGgxHalfway(GetHammersley(i, c.samples), alpha), normal);
        float noh = saturate(dot(normal, halfway));
        float3 toLight = 2.0 * noh * halfway - normal;
        float nol = dot(normal, toLight);
        [branch] if (nol <= 0.0)
        {
            continue;
        }

        // With the view the normal, n.h = v.h, and the sample's density over the sphere is D / 4.
        float sampleAngle = 4.0 / ((float)c.samples * max(GetGgxDistribution(noh, alpha), 1e-6));
        float read = clamp(0.5 * log2(sampleAngle / texelAngle) + 1.0, 0.0, (float)captured.z - 1.0);
        sum += c.cube.SampleLevel(c.sampler, toLight, read).rgb * nol;
        weights += nol;
    }

    return sum / max(weights, 1e-6);
}

[shader("compute")]
[numthreads(8, 8, 1)]
void FilterSkyLight(
    uint3 group : SV_GroupID,
    uint3 id : SV_DispatchThreadID,
    uint thread : SV_GroupIndex,
    uniform PassHeader pass,
    uniform FilterConstants c)
{
    uint level = id.z / 6u;
    uint face = id.z % 6u;
    if (level >= c.levels)
    {
        if (all(group.xy == 0u))
        {
            ProjectSkySh(c, thread);
        }

        return;
    }

    FacesRef faces = c.specular.GetLevel(level);
    uint2 size = faces.size;
    if (any(id.xy >= size))
    {
        return;
    }

    float3 normal = GetCubeDirection(face, (float2(id.xy) + 0.5) / float2(size));
    faces[uint3(id.xy, face)] = float4(PrefilterTexel(c, normal, level), 1.0);
}

[shader("compute")]
[numthreads(8, 8, 1)]
void DrawSky<K : ISky>(uint3 id : SV_DispatchThreadID, uniform PassHeader pass, uniform SkyConstants c)
{
    BeginPass(pass);
    if (any(id.xy >= (uint2)View.viewSize) || c.depth.Load(int2(id.xy), 0).r > 0.0)
    {
        return;
    }

    float2 pixel = float2(id.xy) + 0.5;
    float3 direction = GetPixelDirection(pixel);
    float3 directionDdx = GetPixelDirection(pixel + float2(1.0, 0.0)) - direction;
    float3 directionDdy = GetPixelDirection(pixel + float2(0.0, 1.0)) - direction;
    float dither = GetInterleavedGradientNoise(pixel, World.clocks.index);
    SkyRadiance light = EvaluateSky<K>(CreateSkyInputs(direction, directionDdx, directionDdy, false, dither));
    float exposure = View.exposure[0u].x;
    c.target.GetLevel(0u)[id.xy] = float4((light.nits + light.discs) * exposure + light.emissive, 1.0);
}
