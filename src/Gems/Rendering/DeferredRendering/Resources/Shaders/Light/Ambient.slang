// The ambient light's terms that gems make for a view, a row of the frame's buffer, C#'s GpuAmbient: the ambient
// occlusion and its bent normal, the sky's irradiance and its reflection map, and how much of the sky each pixel sees.
// An id of 0 is a term that no gem makes.
module "Light/Ambient";

import Core.Heap;
import Core.Frame;
import Core.Math;
import Core.Pack;
import Scene.Shadows;

// Where the ambient terms are in the frame's buffer, right after the local lights' shadows, and their bytes.
public static const uint AmbientOffset = LocalShadowsOffset + LocalShadowsBytes;
public static const uint AmbientBytes = 32u;

public struct AmbientTerms
{
    // The visibility of the ambient light at each pixel, and the direction it is seen from, octahedral in world axes.
    public uint occlusion;
    public uint bentNormal;
    // The sky's irradiance as 9 coefficients of RGB spherical harmonics, and its prefiltered octahedral map.
    public uint skyIrradiance;
    public uint skyMap;
    public uint skyMapLevels;
    // How much of the sky each pixel sees, diffuse in red and specular in green, and the sampler of the map and of it.
    public uint skyVisibility;
    public uint sampler;
    // The sun's light that one bounce brings to each pixel, at half size.
    public uint bounce;
}

public AmbientTerms GetAmbientTerms() { return LoadFrameRow<AmbientTerms>(AmbientOffset); }

// The ambient occlusion at a pixel, 1 where no gem makes it.
public float LoadAmbientOcclusion(AmbientTerms terms, uint2 pixel)
{
    if (terms.occlusion == 0u)
        return 1.0;

    TextureRef texture = { terms.occlusion };
    return texture.Load(pixel, 0u).x;
}

// The bent normal at a pixel, or the surface's normal where no gem makes it.
public float3 LoadBentNormal(AmbientTerms terms, uint2 pixel, float3 normal)
{
    if (terms.bentNormal == 0u)
        return normal;

    TextureRef texture = { terms.bentNormal };
    return NormalizeSafe(DecodeUnitVectorUnorm(texture.Load(pixel, 0u).xy), normal);
}

// The visibility of the ambient diffuse light after its bounces between the surfaces that hide it, from the
// visibility and the albedo: Jimenez et al., "Practical Real-Time Strategies for Accurate Indirect Occlusion", 2016,
// section 5, equation 10.
public float3 GetMultiBounceOcclusion(float visibility, float3 albedo)
{
    float3 a = 2.0404 * albedo - 0.3324;
    float3 b = -4.7951 * albedo + 0.6417;
    float3 c = 2.7552 * albedo + 0.6903;
    return max(float3(visibility), ((visibility * a + b) * visibility + c) * visibility);
}

// The visibility of the ambient specular light from the ambient occlusion, the cosine between the normal and the view,
// and the roughness: Lagarde and de Rousiers, "Moving Frostbite to Physically Based Rendering", 2014, listing 26.
public float GetSpecularOcclusion(float visibility, float facing, float roughness)
{
    return saturate(pow(facing + visibility, exp2(-16.0 * roughness - 1.0)) - 1.0 + visibility);
}

// The coefficients of the sky's irradiance buffer: 9 of RGB spherical harmonics up to the second band, then the
// radiance of the ground below the horizon, each a float4.
public static const uint SkyHarmonics = 9u;
public static const uint SkyGroundRow = 9u;
public static const uint SkyIrradianceRows = 10u;

// The real spherical harmonics up to the second band at a unit direction, in the order of the buffer.
public void GetHarmonics(float3 d, out float basis[9])
{
    basis[0] = 0.282095;
    basis[1] = 0.488603 * d.y;
    basis[2] = 0.488603 * d.z;
    basis[3] = 0.488603 * d.x;
    basis[4] = 1.092548 * d.x * d.y;
    basis[5] = 1.092548 * d.y * d.z;
    basis[6] = 0.315392 * (3.0 * d.z * d.z - 1.0);
    basis[7] = 1.092548 * d.x * d.z;
    basis[8] = 0.546274 * (d.x * d.x - d.y * d.y);
}

// The irradiance on a surface facing a unit normal from radiance as 9 coefficients of RGB spherical harmonics, each
// band times the cosine lobe's own: pi, 2 pi / 3 and pi / 4 (Ramamoorthi and Hanrahan, "An Efficient Representation
// for Irradiance Environment Maps", 2001, equation 9).
public float3 GetIrradiance(ArrayRef<float4> harmonics, float3 normal)
{
    float basis[9];
    GetHarmonics(normal, basis);
    static const float bands[9] = { Pi, 2.0 * Pi / 3.0, 2.0 * Pi / 3.0, 2.0 * Pi / 3.0, Pi / 4.0, Pi / 4.0, Pi / 4.0,
        Pi / 4.0, Pi / 4.0 };
    float3 irradiance = float3(0.0);
    for (uint i = 0u; i < SkyHarmonics; i++)
        irradiance += bands[i] * basis[i] * harmonics[i].rgb;

    return max(irradiance, float3(0.0));
}

// The sky's irradiance on a surface facing a unit normal, where a gem projected it.
public float3 GetSkyIrradiance(AmbientTerms terms, float3 normal)
{
    ArrayRef<float4> harmonics = { terms.skyIrradiance };
    return GetIrradiance(harmonics, normal);
}

// The texture coordinates of a unit direction in an octahedral map of the whole sphere.
public float2 ToOctahedralUv(float3 direction) { return EncodeOctahedral(direction) * 0.5 + 0.5; }

// The unit direction at texture coordinates of an octahedral map of the whole sphere.
public float3 FromOctahedralUv(float2 uv) { return DecodeOctahedral(uv * 2.0 - 1.0); }

// The sky's radiance along a direction, prefiltered for a perceptual roughness: the level that is roughness times the
// last.
public float3 SampleSkyMap(AmbientTerms terms, float3 direction, float roughness)
{
    TextureRef map = { terms.skyMap };
    SamplerRef sampler = { terms.sampler };
    float level = saturate(roughness) * float(max(terms.skyMapLevels, 1u) - 1u);
    return map.SampleLevel(sampler, ToOctahedralUv(direction), level).rgb;
}

// The share of the sky a pixel sees, diffuse in x and specular in y, from the half-size visibility. All of the sky
// where no gem makes it.
public float2 LoadSkyVisibility(AmbientTerms terms, FrameConstants frame, uint2 pixel, float depth, float3 normal)
{
    if (terms.skyVisibility == 0u)
        return float2(1.0);

    return LoadHalfSize(terms.skyVisibility, frame, pixel, depth, normal).xy;
}

// The sun's light that one bounce brings to a pixel, from the half-size bounce. None where no gem makes it.
public float3 LoadBounce(AmbientTerms terms, FrameConstants frame, uint2 pixel, float depth, float3 normal)
{
    if (terms.bounce == 0u)
        return float3(0.0);

    return LoadHalfSize(terms.bounce, frame, pixel, depth, normal).rgb;
}

// A half-size texture at a pixel: the 4 texels around it, each weighed by how near the depth and the normal of the
// pixel it stands for are to this pixel's.
float4 LoadHalfSize(uint id, FrameConstants frame, uint2 pixel, float depth, float3 normal)
{
    TextureRef visibility = { id };
    TextureRef depthTexture = { frame.depth };
    int2 size = int2(visibility.GetSize(0u));
    int2 full = int2(frame.width, frame.height) - 1;
    int2 base = int2(floor((float2(pixel) + 0.5) * 0.5 - 0.5));
    float2 f = frac((float2(pixel) + 0.5) * 0.5 - 0.5);
    float viewDepth = frame.near / max(depth, 1e-12);
    float4 sum = float4(0.0);
    float weight = 0.0;
    for (uint i = 0u; i < 4u; i++)
    {
        int2 offset = int2(i & 1u, i >> 1u);
        int2 at = clamp(base + offset, int2(0), size - 1);
        uint2 atPixel = uint2(min(at * 2, full));
        float atDepth = depthTexture.Load(atPixel, 0u).x;
        float bilinear = (offset.x == 0 ? 1.0 - f.x : f.x) * (offset.y == 0 ? 1.0 - f.y : f.y);
        float depthWeight = atDepth > 0.0
            ? exp(-abs(frame.near / atDepth - viewDepth) / max(0.05 * viewDepth, 1e-3))
            : 0.0;
        float normalWeight = pow(saturate(dot(LoadNormal(frame.gbufferNormal, atPixel), normal)), 8.0);
        float w = bilinear * depthWeight * normalWeight + bilinear * 1e-4;
        sum += visibility.Load(uint2(at), 0u) * w;
        weight += w;
    }

    return sum / max(weight, 1e-8);
}
