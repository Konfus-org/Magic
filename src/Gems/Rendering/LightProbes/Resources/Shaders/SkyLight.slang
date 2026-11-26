// The sky's light for the ambient light of a view, each frame from the sky as it is: its irradiance as spherical
// harmonics, and an octahedral map of its radiance with each level prefiltered for a roughness. Below the horizon, the
// sky is the ground, which reflects the sky's and the sun's light on it by the sky's ground albedo. The pass header's x
// is the frame's buffer, its y the buffer of SkyLightConstants, a row a level, and its z the byte offset of the
// pass's row.
module "SkyLight";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Core.Pack;
import Core.Sampling;
import Scene.Lights;
import Light.Ambient;
import Sky.ISky;

// What the passes read beyond the frame, C#'s SkyLightConstants, 32 bytes.
struct SkyLightConstants
{
    // The irradiance buffer, the source map with its levels, the prefiltered map, and the linear sampler.
    uint irradiance;
    uint source;
    uint map;
    uint sampler;
    // The side of level 0 of both maps, their levels, and the level a pass writes.
    uint size;
    uint levels;
    uint level;
    // The importance samples of the prefilter.
    uint samples;
};

// The pass's row of constants: the header's y names the buffer, and its z the row's byte offset.
SkyLightConstants LoadSkyConstants()
{
    BufferRef buffer = { GetPassHeader().y };
    return buffer.Load<SkyLightConstants>(GetPassHeader().z);
}

// The samples of the projection: across in azimuth, a thread each, and down in polar angle from the zenith.
static const uint ProjectColumns = 64u;
static const uint ProjectRows = 32u;

groupshared float3 columnLight[ProjectColumns][SkyHarmonics];
groupshared float columnGround[ProjectColumns][SkyHarmonics];

// The sky's radiance above the horizon, without the sun's disc, whose light the sun's own rays bring.
float3 GetSkyLight<S : ISky>(float3 direction)
{
    return S.Evaluate(CreateSkyInputs(GetLights(), GetSkyTables(), direction, false));
}

// Projects the sky above the horizon onto the harmonics over 64 by 32 directions, in one group; then the ground below
// it, whose radiance is the ground albedo over pi times the light on the ground: the sky's irradiance from above and
// the sun's illuminance on a level surface.
[shader("compute")]
[numthreads(64, 1, 1)]
void ProjectSkyIrradiance<S : ISky>(uint3 thread : SV_GroupThreadID)
{
    SkyLightConstants constants = LoadSkyConstants();
    uint column = thread.x;
    float phi = (float(column) + 0.5) * TwoPi / float(ProjectColumns);
    float3 light[SkyHarmonics];
    float ground[SkyHarmonics];
    for (uint i = 0u; i < SkyHarmonics; i++)
    {
        light[i] = float3(0.0);
        ground[i] = 0.0;
    }

    for (uint row = 0u; row < ProjectRows; row++)
    {
        float theta = (float(row) + 0.5) * Pi / float(ProjectRows);
        float3 direction = float3(sin(theta) * cos(phi), cos(theta), sin(theta) * sin(phi));
        float solidAngle = sin(theta) * (Pi / float(ProjectRows)) * (TwoPi / float(ProjectColumns));
        float basis[SkyHarmonics];
        GetHarmonics(direction, basis);
        if (direction.y >= 0.0)
        {
            float3 radiance = GetSkyLight<S>(direction) * solidAngle;
            for (uint i = 0u; i < SkyHarmonics; i++)
                light[i] += radiance * basis[i];
        }
        else
        {
            for (uint i = 0u; i < SkyHarmonics; i++)
                ground[i] += solidAngle * basis[i];
        }
    }

    for (uint i = 0u; i < SkyHarmonics; i++)
    {
        columnLight[column][i] = light[i];
        columnGround[column][i] = ground[i];
    }

    GroupMemoryBarrierWithGroupSync();
    if (column != 0u)
        return;

    float4 harmonics[SkyHarmonics];
    float groundBasis[SkyHarmonics];
    for (uint i = 0u; i < SkyHarmonics; i++)
    {
        float3 sum = float3(0.0);
        float groundSum = 0.0;
        for (uint c = 0u; c < ProjectColumns; c++)
        {
            sum += columnLight[c][i];
            groundSum += columnGround[c][i];
        }

        harmonics[i] = float4(sum, 0.0);
        groundBasis[i] = groundSum;
    }

    // The light on the ground: the sky's irradiance from above alone, and the sun's through the air.
    float3 up = float3(0.0, 1.0, 0.0);
    float upBasis[SkyHarmonics];
    GetHarmonics(up, upBasis);
    static const float bands[SkyHarmonics] = { Pi, 2.0 * Pi / 3.0, 2.0 * Pi / 3.0, 2.0 * Pi / 3.0, Pi / 4.0,
        Pi / 4.0, Pi / 4.0, Pi / 4.0, Pi / 4.0 };
    float3 skyOnGround = float3(0.0);
    for (uint i = 0u; i < SkyHarmonics; i++)
        skyOnGround += bands[i] * upBasis[i] * harmonics[i].rgb;

    Lights lights = GetLights();
    float3 sunOnGround = GetSunIlluminance<S>(lights) * saturate(lights.sunDirection.y);
    float3 groundRadiance = S.GetGroundAlbedo() * InvPi * (max(skyOnGround, float3(0.0)) + sunOnGround);

    BufferRef output = { constants.irradiance };
    for (uint i = 0u; i < SkyHarmonics; i++)
        output.Store<float4>(i * 16u, float4(harmonics[i].rgb + groundRadiance * groundBasis[i], 0.0));

    output.Store<float4>(SkyGroundRow * 16u, float4(groundRadiance, 0.0));
}

// Draws the sky into level 0 of the source map: above the horizon as it is, below it the ground's radiance.
[shader("compute")]
[numthreads(8, 8, 1)]
void DrawSkyMap<S : ISky>(uint3 thread : SV_DispatchThreadID)
{
    SkyLightConstants constants = LoadSkyConstants();
    if (any(thread.xy >= uint2(constants.size)))
        return;

    float3 direction = FromOctahedralUv((float2(thread.xy) + 0.5) / float(constants.size));
    float3 radiance;
    if (direction.y >= 0.0)
        radiance = GetSkyLight<S>(direction);
    else
    {
        ArrayRef<float4> rows = { constants.irradiance };
        radiance = rows[SkyGroundRow].rgb;
    }

    StorageRef source = { constants.source, 0u };
    source.Store(thread.xy, float4(radiance, 1.0));
}

// Each level of the source map from the one above: the mean of its 2 by 2 texels.
[shader("compute")]
[numthreads(8, 8, 1)]
void BuildSkyMips(uint3 thread : SV_DispatchThreadID)
{
    SkyLightConstants constants = LoadSkyConstants();
    uint size = max(constants.size >> constants.level, 1u);
    if (any(thread.xy >= uint2(size)))
        return;

    TextureRef source = { constants.source };
    uint2 above = thread.xy * 2u;
    uint upper = constants.level - 1u;
    float4 sum = source.Load(above, upper) + source.Load(above + uint2(1u, 0u), upper)
        + source.Load(above + uint2(0u, 1u), upper) + source.Load(above + uint2(1u, 1u), upper);
    StorageRef target = { constants.source, constants.level };
    target.Store(thread.xy, sum * 0.25);
}

// The GGX distribution at the cosine between the normal and the half vector, for alpha.
float GetGgx(float cosine, float alpha)
{
    float a2 = alpha * alpha;
    float d = cosine * cosine * (a2 - 1.0) + 1.0;
    return a2 / max(Pi * d * d, Epsilon);
}

// A level of the prefiltered map: the sky convolved with GGX at the roughness of the level, level over the last, seen
// along its normal, from importance samples each read from the level of the source whose texels are as wide as the
// sample's solid angle (Karis, "Real Shading in Unreal Engine 4", 2013; Křivánek and Colbert, "Real-time Shading with
// Filtered Importance Sampling", 2008). Level 0 is the source itself.
[shader("compute")]
[numthreads(8, 8, 1)]
void PrefilterSkyMap(uint3 thread : SV_DispatchThreadID)
{
    SkyLightConstants constants = LoadSkyConstants();
    uint size = max(constants.size >> constants.level, 1u);
    if (any(thread.xy >= uint2(size)))
        return;

    TextureRef source = { constants.source };
    StorageRef target = { constants.map, constants.level };
    if (constants.level == 0u)
    {
        target.Store(thread.xy, source.Load(thread.xy, 0u));
        return;
    }

    float roughness = float(constants.level) / float(max(constants.levels, 2u) - 1u);
    float alpha = roughness * roughness;
    float3 normal = FromOctahedralUv((float2(thread.xy) + 0.5) / float(size));
    float3 tangent = abs(normal.y) < 0.999 ? normalize(cross(float3(0.0, 1.0, 0.0), normal)) : float3(1.0, 0.0, 0.0);
    float3 bitangent = cross(normal, tangent);
    SamplerRef sampler = { constants.sampler };
    float texelAngle = 4.0 * Pi / float(constants.size * constants.size);
    float lastLevel = float(constants.levels - 1u);
    float3 sum = float3(0.0);
    float weight = 0.0;
    for (uint i = 0u; i < constants.samples; i++)
    {
        float2 xi = GetHammersley(i, constants.samples);
        float cosHalf = sqrt((1.0 - xi.y) / (1.0 + (alpha * alpha - 1.0) * xi.y));
        float sinHalf = sqrt(max(1.0 - cosHalf * cosHalf, 0.0));
        float phi = TwoPi * xi.x;
        float3 half = tangent * (sinHalf * cos(phi)) + bitangent * (sinHalf * sin(phi)) + normal * cosHalf;
        float3 light = 2.0 * dot(normal, half) * half - normal;
        float cosine = dot(normal, light);
        if (cosine <= 0.0)
            continue;

        // With the view along the normal, the density of the reflected direction is D over 4.
        float density = GetGgx(cosHalf, alpha) * 0.25;
        float sampleAngle = 1.0 / max(float(constants.samples) * density, Epsilon);
        float level = clamp(0.5 * log2(sampleAngle / texelAngle) + 1.0, 0.0, lastLevel);
        sum += source.SampleLevel(sampler, ToOctahedralUv(light), level).rgb * cosine;
        weight += cosine;
    }

    target.Store(thread.xy, float4(sum / max(weight, Epsilon), 1.0));
}
