// What the translucent surfaces leave for the frame, C#'s TranslucencyConstants: the moments of the panes in front
// of every pixel, the light they add and the picture they bend, the sun's colour through them on everything behind
// them, and the panes a ray crosses. The order of the panes is solved from the moments of their depths, with no
// sorting (Münstermann et al., "Moment-Based Order-Independent Transparency", 2018). The constants' heap id is the
// lighting constants' translucency.
module "Scene/Translucency";

import Core.Heap;
import Core.Pass;
import Core.Math;
import Scene.Lighting;

public static const uint GlassMax = 64u;

public struct TranslucencyConstants
{
    // The world to the sun's glass shadow, orthographic, row-major, for row vectors.
    public float4x4 sunViewProjection;
    // Heap ids.
    public uint moments;
    public uint opticalDepth;
    public uint accumulation;
    public uint offsets;
    public uint glassShadow;
    public uint glassList;
    public uint glassCount;
    public uint sampler;
    // Metres: the depths the moments are warped between.
    public float near;
    public float far;
    public uint hasTranslucent;
    public uint padding;
}

// A translucent instance as a sphere, for the rays that cross it: C#'s GpuGlass.
public struct GpuGlass
{
    public float3 center;
    public float radius;
    public float3 tint;
    public float padding;
}

public TranslucencyConstants GetTranslucency() { return LoadConstants<TranslucencyConstants>(GetLighting().translucency); }

// A view depth as the moments take it: -1 at the near depth to 1 at the far, in the log of the depth.
public float WarpDepth(TranslucencyConstants constants, float viewDepth)
{
    float t = (log(clamp(viewDepth, constants.near, constants.far)) - log(constants.near)) / (log(constants.far) - log(constants.near));
    return t * 2.0 - 1.0;
}

// How much reaches a warped depth through the panes in front of it, from the total absorbance and the four power
// moments of the panes' depths, each already divided by the total: the paper's reconstruction with its bias.
public float TransmittanceFromMoments(float b0, float4 moments, float z)
{
    float4 b = lerp(moments, float4(0.0, 0.375, 0.0, 0.375), 5e-7);
    float3 depths;
    depths[0] = z;
    float l21d11 = mad(-b[0], b[1], b[2]);
    float d11 = mad(-b[0], b[0], b[1]);
    float inverseD11 = 1.0 / d11;
    float l21 = l21d11 * inverseD11;
    float squaredDepthVariance = mad(-b[1], b[1], b[3]);
    float d22 = mad(-l21d11, l21, squaredDepthVariance);
    float3 c = float3(1.0, z, z * z);
    c[1] -= b.x;
    c[2] -= b.y + l21 * c[1];
    c[1] *= inverseD11;
    c[2] /= d22;
    c[1] -= l21 * c[2];
    c[0] -= dot(c.yz, b.xy);
    float inverseC2 = 1.0 / c[2];
    float p = c[1] * inverseC2;
    float q = c[0] * inverseC2;
    float discriminant = (p * p * 0.25) - q;
    float r = sqrt(max(discriminant, 0.0));
    depths[1] = -p * 0.5 - r;
    depths[2] = -p * 0.5 + r;
    float3 weights = float3(0.25, depths[1] < depths[0] ? 1.0 : 0.0, depths[2] < depths[0] ? 1.0 : 0.0);
    float f01 = (weights[1] - weights[0]) / (depths[1] - depths[0]);
    float f12 = (weights[2] - weights[1]) / (depths[2] - depths[1]);
    float f012 = (f12 - f01) / (depths[2] - depths[0]);
    float3 polynomial;
    polynomial[0] = f012;
    polynomial[1] = polynomial[0];
    polynomial[0] = f01 - polynomial[0] * depths[1];
    polynomial[2] = polynomial[1];
    polynomial[1] = polynomial[0] - polynomial[1] * depths[0];
    polynomial[0] = weights[0] - polynomial[0] * depths[0];
    float absorbance = polynomial[0] + dot(b.xy, polynomial.yz);
    return saturate(exp(-b0 * absorbance));
}

// The sun's colour through the panes at a point: one where no pane is in the way, the pane's tint where one is.
public float3 GetGlassShadow(TranslucencyConstants constants, float3 world)
{
    if (constants.hasTranslucent == 0u)
        return float3(1.0);

    float4 clip = mul(float4(world, 1.0), constants.sunViewProjection);
    float2 uv = float2(clip.x * 0.5 + 0.5, 0.5 - clip.y * 0.5);
    if (any(uv < 0.0) || any(uv > 1.0))
        return float3(1.0);

    TextureRef shadow = { constants.glassShadow };
    SamplerRef sampler = { constants.sampler };
    return shadow.SampleLevel(sampler, uv, 0.0).rgb;
}

// What reaches the end of a segment through the translucent instances it crosses, each a sphere with a tint.
public float3 GetGlassTransmittance(TranslucencyConstants constants, float3 from, float3 to)
{
    float3 transmittance = float3(1.0);
    if (constants.glassCount == 0u)
        return transmittance;

    float3 segment = to - from;
    float length = max(length(segment), Epsilon);
    float3 direction = segment / length;
    ArrayRef<GpuGlass> list = { constants.glassList };
    for (uint i = 0u; i < min(constants.glassCount, GlassMax); i++)
    {
        GpuGlass glass = list[i];
        float3 offset = from - glass.center;
        float b = dot(offset, direction);
        float c = dot(offset, offset) - glass.radius * glass.radius;
        float discriminant = b * b - c;
        if (discriminant < 0.0)
            continue;

        float root = sqrt(discriminant);
        float t0 = -b - root;
        float t1 = -b + root;
        if (t1 < 0.0 || t0 > length)
            continue;

        transmittance *= glass.tint;
    }

    return transmittance;
}
