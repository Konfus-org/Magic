// Ground-truth ambient occlusion of each pixel of a view, ported from Intel's XeGTAO (XeGTAO.hlsli, MIT licence; see
// THIRD-PARTY-NOTICES.md beside this file), which follows Jimenez et al., "Practical Real-Time Strategies for Accurate
// Indirect Occlusion", 2016. Three passes: the view depth in 5 levels, each a weighted mean of the one above; the
// visibility and the bent normal along a few slices across the picture, with the edges between pixels; and a blur
// that keeps to the edges, which writes the visibility and the bent normal in world axes. The pass header's x is the
// frame's buffer and its y the buffer of GtaoConstants.
//
// Changes from XeGTAO: the view depth comes from reversed depth with no far plane, as the near plane over the depth;
// the normal is the GBuffer's, turned into view axes; the working term is an R8G8B8A8Unorm texture, not a packed
// R32Uint; the blur reads its neighbours by loads, a pixel a thread; the visibility fades to none at the picture's edges.
module "Gtao";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Core.Pack;

// What the passes read beyond the frame, C#'s GtaoConstants, 96 bytes, the buffer the pass header's y names.
struct GtaoConstants
{
    // The view depth's 5 levels, the working visibility and bent normal, the packed edges, the outputs, and the point
    // sampler that clamps.
    uint viewDepths;
    uint working;
    uint edges;
    uint occlusion;
    uint bentNormal;
    uint sampler;
    uint isBentNormalOn;
    // The frame's index modulo 64, which turns the noise of each pixel each frame for the TAA to blend.
    uint noiseIndex;
    float2 viewportPixelSize;
    // From texture coordinates to a view direction at a depth of 1: x right, y up, z forward.
    float2 ndcToViewMul;
    float2 ndcToViewAdd;
    float2 ndcToViewMulPixel;
    // Metres of the occlusion's sphere, already times XeGTAO's 1.457, and the share of it over which a sample fades.
    float effectRadius;
    float falloffRange;
    float finalValuePower;
    float denoiseBlurBeta;
    float depthMipSamplingOffset;
    float sliceCount;
    float stepsPerSlice;
    // The share of the picture's width and height, at each side, over which the occlusion fades to none.
    float edgeFade;
};

static const uint DepthMipLevels = 5u;

// The working visibility is stored over this, as a raw one can overshoot 1 before the blur.
static const float OcclusionTermScale = 1.5;

// XeGTAO's default constants.
static const float SampleDistributionPower = 2.0;
static const float PixelTooCloseThreshold = 1.3;

// The view depth at a pixel of the picture whose reversed depth is given: the near plane over it, and the far limit of
// a 32-bit float where nothing was drawn.
float ToViewDepth(FrameConstants frame, float depth)
{
    return depth > 0.0 ? min(frame.near / depth, 3.0e38) : 3.0e38;
}

float3 GetViewPosition(GtaoConstants constants, float2 uv, float viewDepth)
{
    return float3((constants.ndcToViewMul * uv + constants.ndcToViewAdd) * viewDepth, viewDepth);
}

// The Hilbert curve's index of a pixel of a 64-pixel square: XeGTAO.h's HilbertIndex.
uint GetHilbertIndex(uint x, uint y)
{
    uint index = 0u;
    for (uint level = 32u; level > 0u; level /= 2u)
    {
        uint regionX = (x & level) > 0u ? 1u : 0u;
        uint regionY = (y & level) > 0u ? 1u : 0u;
        index += level * level * ((3u * regionX) ^ regionY);
        if (regionY == 0u)
        {
            if (regionX == 1u)
            {
                x = 63u - x;
                y = 63u - y;
            }

            uint swap = x;
            x = y;
            y = swap;
        }
    }

    return index;
}

// Noise along the Hilbert curve by the R2 sequence, turned each frame: XeGTAO's SpatioTemporalNoise.
float2 GetSpatioTemporalNoise(uint2 pixel, uint temporalIndex)
{
    uint index = GetHilbertIndex(pixel.x % 64u, pixel.y % 64u) + 288u * (temporalIndex % 64u);
    return frac(0.5 + float(index) * float2(0.75487766624669276005, 0.5698402909980532659114));
}

// The weighted mean of 4 view depths that the next level keeps: XeGTAO_DepthMIPFilter.
float FilterDepths(GtaoConstants constants, float4 depths)
{
    float maxDepth = max(max(depths.x, depths.y), max(depths.z, depths.w));
    float effectRadius = 0.75 * constants.effectRadius;
    float falloffRange = constants.falloffRange * effectRadius;
    float falloffFrom = effectRadius * (1.0 - constants.falloffRange);
    float falloffMul = -1.0 / falloffRange;
    float falloffAdd = falloffFrom / falloffRange + 1.0;
    float4 weights = saturate((maxDepth - depths) * falloffMul + falloffAdd);
    return dot(weights, depths) / max(dot(weights, float4(1.0)), Epsilon);
}

groupshared float scratchDepths[8][8];

void StoreDepth(uint id, uint level, uint2 texel, uint2 size, float depth)
{
    uint2 levelSize = max(size >> level, uint2(1u));
    if (all(texel < levelSize))
    {
        StorageRef target = { id, level };
        target.Store(texel, float4(depth, 0.0, 0.0, 0.0));
    }
}

// Each thread makes 2 by 2 pixels of the first level and one of the second, and a group of 8 by 8 the rest: XeGTAO's
// XeGTAO_PrefilterDepths16x16.
[shader("compute")]
[numthreads(8, 8, 1)]
void PrefilterDepths(uint3 thread : SV_DispatchThreadID, uint3 local : SV_GroupThreadID)
{
    FrameConstants frame = GetFrame();
    GtaoConstants constants = LoadConstants<GtaoConstants>(GetPassHeader().y);
    uint2 size = uint2(frame.width, frame.height);
    TextureRef depthTexture = { frame.depth };
    uint2 pixel = thread.xy * 2u;
    float4 depths;
    depths.x = ToViewDepth(frame, depthTexture.Load(min(pixel, size - 1u), 0u).x);
    depths.y = ToViewDepth(frame, depthTexture.Load(min(pixel + uint2(1u, 0u), size - 1u), 0u).x);
    depths.z = ToViewDepth(frame, depthTexture.Load(min(pixel + uint2(0u, 1u), size - 1u), 0u).x);
    depths.w = ToViewDepth(frame, depthTexture.Load(min(pixel + uint2(1u, 1u), size - 1u), 0u).x);
    StoreDepth(constants.viewDepths, 0u, pixel, size, depths.x);
    StoreDepth(constants.viewDepths, 0u, pixel + uint2(1u, 0u), size, depths.y);
    StoreDepth(constants.viewDepths, 0u, pixel + uint2(0u, 1u), size, depths.z);
    StoreDepth(constants.viewDepths, 0u, pixel + uint2(1u, 1u), size, depths.w);

    float level1 = FilterDepths(constants, depths);
    StoreDepth(constants.viewDepths, 1u, thread.xy, size, level1);
    scratchDepths[local.x][local.y] = level1;
    GroupMemoryBarrierWithGroupSync();

    for (uint level = 2u; level < DepthMipLevels; level++)
    {
        uint step = 1u << (level - 2u);
        bool isKept = all((local.xy % (step * 2u)) == 0u);
        float filtered = 0.0;
        if (isKept)
        {
            float4 quad = float4(
                scratchDepths[local.x][local.y],
                scratchDepths[local.x + step][local.y],
                scratchDepths[local.x][local.y + step],
                scratchDepths[local.x + step][local.y + step]);
            filtered = FilterDepths(constants, quad);
            StoreDepth(constants.viewDepths, level, thread.xy >> (level - 1u), size, filtered);
        }

        GroupMemoryBarrierWithGroupSync();
        if (isKept)
            scratchDepths[local.x][local.y] = filtered;

        GroupMemoryBarrierWithGroupSync();
    }
}

// How much each of the 4 neighbours, left, right, top and bottom, lies on the same surface: XeGTAO_CalculateEdges.
float4 CalculateEdges(float centerZ, float leftZ, float rightZ, float topZ, float bottomZ)
{
    float4 edges = float4(leftZ, rightZ, topZ, bottomZ) - centerZ;
    float slopeLR = (edges.y - edges.x) * 0.5;
    float slopeTB = (edges.w - edges.z) * 0.5;
    float4 slopeAdjusted = edges + float4(slopeLR, -slopeLR, slopeTB, -slopeTB);
    edges = min(abs(edges), abs(slopeAdjusted));
    return saturate(1.25 - edges / (centerZ * 0.011));
}

float PackEdges(float4 edges)
{
    edges = round(saturate(edges) * 2.9);
    return dot(edges, float4(64.0 / 255.0, 16.0 / 255.0, 4.0 / 255.0, 1.0 / 255.0));
}

float4 UnpackEdges(float packed)
{
    uint value = uint(packed * 255.5);
    return saturate(float4((value >> 6u) & 3u, (value >> 4u) & 3u, (value >> 2u) & 3u, value & 3u) / 3.0);
}

// Input from -1 to 1, output from 0 to pi: Lagarde's fit, as XeGTAO_FastACos.
float FastAcos(float x)
{
    float magnitude = abs(x);
    float result = (-0.156583 * magnitude + HalfPi) * sqrt(max(1.0 - magnitude, 0.0));
    return x >= 0.0 ? result : Pi - result;
}

// The rotation that turns from into to: XeGTAO_RotFromToMatrix, after Möller and Hughes, 1999.
float3x3 GetRotation(float3 from, float3 to)
{
    float e = dot(from, to);
    if (abs(e) > 1.0 - 0.0003)
        return float3x3(1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0);

    float3 v = cross(from, to);
    float h = 1.0 / (1.0 + e);
    float hvx = h * v.x;
    float hvz = h * v.z;
    float hvxy = hvx * v.y;
    float hvxz = hvx * v.z;
    float hvyz = hvz * v.y;
    return float3x3(
        e + hvx * v.x, hvxy - v.z, hvxz + v.y,
        hvxy + v.z, e + h * v.y * v.y, hvyz - v.x,
        hvxz - v.y, hvyz + v.x, e + hvz * v.z);
}

float LoadViewDepth(GtaoConstants constants, int2 pixel, int2 size)
{
    TextureRef depths = { constants.viewDepths };
    return depths.Load(uint2(clamp(pixel, int2(0), size - 1)), 0u).x;
}

// The visibility and bent normal of each pixel along the slices, before the blur: XeGTAO_MainPass, algorithms 1 and 2
// of the paper.
[shader("compute")]
[numthreads(8, 8, 1)]
void GtaoMainPass(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    GtaoConstants constants = LoadConstants<GtaoConstants>(GetPassHeader().y);
    int2 size = int2(frame.width, frame.height);
    int2 pixel = int2(thread.xy);
    if (any(pixel >= size))
        return;

    StorageRef working = { constants.working, 0u };
    StorageRef edgesTarget = { constants.edges, 0u };
    float viewZ = LoadViewDepth(constants, pixel, size);
    float leftZ = LoadViewDepth(constants, pixel + int2(-1, 0), size);
    float rightZ = LoadViewDepth(constants, pixel + int2(1, 0), size);
    float topZ = LoadViewDepth(constants, pixel + int2(0, -1), size);
    float bottomZ = LoadViewDepth(constants, pixel + int2(0, 1), size);
    float4 edges = CalculateEdges(viewZ, leftZ, rightZ, topZ, bottomZ);
    edgesTarget.Store(thread.xy, float4(PackEdges(edges), 0.0, 0.0, 0.0));

    float3 worldNormal = LoadNormal(frame.gbufferNormal, thread.xy);
    float3 viewNormal = NormalizeSafe(mul(float4(worldNormal, 0.0), frame.view).xyz, float3(0.0, 0.0, -1.0));
    TextureRef depthTexture = { frame.depth };
    if (depthTexture.Load(thread.xy, 0u).x == 0.0)
    {
        working.Store(thread.xy, float4(viewNormal * 0.5 + 0.5, 1.0 / OcclusionTermScale));
        return;
    }

    // Moved a little towards the camera, against the depth's error.
    viewZ *= 0.99999;
    float2 uv = (float2(pixel) + 0.5) * constants.viewportPixelSize;
    float3 center = GetViewPosition(constants, uv, viewZ);
    float3 viewVec = normalize(-center);

    float effectRadius = constants.effectRadius;
    float falloffRange = constants.falloffRange * effectRadius;
    float falloffFrom = effectRadius * (1.0 - constants.falloffRange);
    float falloffMul = -1.0 / falloffRange;
    float falloffAdd = falloffFrom / falloffRange + 1.0;

    float2 noise = GetSpatioTemporalNoise(thread.xy, constants.noiseIndex);
    float2 pixelSizeAtZ = viewZ * constants.ndcToViewMulPixel;
    float screenRadius = effectRadius / pixelSizeAtZ.x;
    float visibility = saturate((10.0 - screenRadius) / 100.0) * 0.5;
    float3 bentNormal = float3(0.0);
    float minS = PixelTooCloseThreshold / screenRadius;
    TextureRef depths = { constants.viewDepths };
    SamplerRef pointSampler = { constants.sampler };

    for (float slice = 0.0; slice < constants.sliceCount; slice += 1.0)
    {
        float phi = (slice + noise.x) / constants.sliceCount * Pi;
        float cosPhi = cos(phi);
        float sinPhi = sin(phi);
        float2 omega = float2(cosPhi, -sinPhi) * screenRadius;
        float3 direction = float3(cosPhi, sinPhi, 0.0);
        float3 orthoDirection = direction - dot(direction, viewVec) * viewVec;
        float3 axis = normalize(cross(orthoDirection, viewVec));
        float3 projectedNormal = viewNormal - axis * dot(viewNormal, axis);
        float signNormal = sign(dot(orthoDirection, projectedNormal));
        float projectedLength = length(projectedNormal);
        float cosNormal = saturate(dot(projectedNormal, viewVec) / max(projectedLength, Epsilon));
        float n = signNormal * FastAcos(cosNormal);
        float lowHorizonCos0 = cos(n + HalfPi);
        float lowHorizonCos1 = cos(n - HalfPi);
        float horizonCos0 = lowHorizonCos0;
        float horizonCos1 = lowHorizonCos1;

        for (float step = 0.0; step < constants.stepsPerSlice; step += 1.0)
        {
            // The R1 sequence along the steps.
            float stepNoise = frac(noise.y + (slice + step * constants.stepsPerSlice) * 0.6180339887498948482);
            float s = pow((step + stepNoise) / constants.stepsPerSlice, SampleDistributionPower) + minS;
            float2 offset = s * omega;
            float mipLevel = clamp(log2(length(offset)) - constants.depthMipSamplingOffset, 0.0, float(DepthMipLevels));
            offset = round(offset) * constants.viewportPixelSize;

            float2 uv0 = uv + offset;
            float2 uv1 = uv - offset;
            float3 delta0 = GetViewPosition(constants, uv0, depths.SampleLevel(pointSampler, uv0, mipLevel).x) - center;
            float3 delta1 = GetViewPosition(constants, uv1, depths.SampleLevel(pointSampler, uv1, mipLevel).x) - center;
            float distance0 = length(delta0);
            float distance1 = length(delta1);
            float weight0 = saturate(distance0 * falloffMul + falloffAdd);
            float weight1 = saturate(distance1 * falloffMul + falloffAdd);
            float shc0 = dot(delta0 / max(distance0, Epsilon), viewVec);
            float shc1 = dot(delta1 / max(distance1, Epsilon), viewVec);
            horizonCos0 = max(horizonCos0, lerp(lowHorizonCos0, shc0, weight0));
            horizonCos1 = max(horizonCos1, lerp(lowHorizonCos1, shc1, weight1));
        }

        // XeGTAO's own fudge against a slight darkening on steep slopes.
        projectedLength = lerp(projectedLength, 1.0, 0.05);
        float h0 = -FastAcos(horizonCos1);
        float h1 = FastAcos(horizonCos0);
        float arc0 = (cosNormal + 2.0 * h0 * sin(n) - cos(2.0 * h0 - n)) / 4.0;
        float arc1 = (cosNormal + 2.0 * h1 * sin(n) - cos(2.0 * h1 - n)) / 4.0;
        visibility += projectedLength * (arc0 + arc1);

        if (constants.isBentNormalOn != 0u)
        {
            float t0 = (6.0 * sin(h0 - n) - sin(3.0 * h0 - n) + 6.0 * sin(h1 - n) - sin(3.0 * h1 - n) + 16.0 * sin(n)
                - 3.0 * (sin(h0 + n) + sin(h1 + n))) / 12.0;
            float t1 = (-cos(3.0 * h0 - n) - cos(3.0 * h1 - n) + 8.0 * cos(n) - 3.0 * (cos(h0 + n) + cos(h1 + n)))
                / 12.0;
            float3 localBent = float3(direction.x * t0, direction.y * t0, -t1);
            bentNormal += mul(GetRotation(float3(0.0, 0.0, -1.0), viewVec), localBent) * projectedLength;
        }
    }

    visibility /= constants.sliceCount;
    visibility = max(0.03, pow(visibility, constants.finalValuePower));
    bentNormal = constants.isBentNormalOn != 0u ? NormalizeSafe(bentNormal, viewNormal) : viewNormal;
    working.Store(thread.xy, float4(bentNormal * 0.5 + 0.5, saturate(visibility / OcclusionTermScale)));
}

float4 LoadWorking(GtaoConstants constants, int2 pixel, int2 size)
{
    TextureRef working = { constants.working };
    float4 value = working.Load(uint2(clamp(pixel, int2(0), size - 1)), 0u);
    return float4(value.xyz * 2.0 - 1.0, value.w);
}

float4 LoadEdges(GtaoConstants constants, int2 pixel, int2 size)
{
    TextureRef edges = { constants.edges };
    return UnpackEdges(edges.Load(uint2(clamp(pixel, int2(0), size - 1)), 0u).x);
}

// The blur of the visibility and the bent normal over each pixel's 8 neighbours, as far as each lies on the same
// surface, the one pass of XeGTAO_Denoise with its final apply; then the fade at the picture's edges, and the bent
// normal into world axes.
[shader("compute")]
[numthreads(8, 8, 1)]
void GtaoDenoise(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    GtaoConstants constants = LoadConstants<GtaoConstants>(GetPassHeader().y);
    int2 size = int2(frame.width, frame.height);
    int2 pixel = int2(thread.xy);
    if (any(pixel >= size))
        return;

    float4 edgesC = LoadEdges(constants, pixel, size);
    float4 edgesL = LoadEdges(constants, pixel + int2(-1, 0), size);
    float4 edgesR = LoadEdges(constants, pixel + int2(1, 0), size);
    float4 edgesT = LoadEdges(constants, pixel + int2(0, -1), size);
    float4 edgesB = LoadEdges(constants, pixel + int2(0, 1), size);

    // Each edge as both of its pixels see it, so the blur is the same from either side.
    edgesC *= float4(edgesL.y, edgesR.x, edgesT.w, edgesB.z);

    // A little of the neighbours leaks in where 3 or 4 edges are, against aliasing.
    float edginess = saturate(4.0 - 2.5 - dot(edgesC, float4(1.0))) / (4.0 - 2.5) * 0.5;
    edgesC = saturate(edgesC + edginess);

    float diagonal = 0.85 * 0.5;
    float weightTL = diagonal * (edgesC.x * edgesL.z + edgesC.z * edgesT.x);
    float weightTR = diagonal * (edgesC.z * edgesT.y + edgesC.y * edgesR.z);
    float weightBL = diagonal * (edgesC.w * edgesB.x + edgesC.x * edgesL.w);
    float weightBR = diagonal * (edgesC.y * edgesR.w + edgesC.w * edgesB.y);

    float sumWeight = constants.denoiseBlurBeta;
    float4 sum = LoadWorking(constants, pixel, size) * sumWeight;
    float weights[8] = { edgesC.x, edgesC.y, edgesC.z, edgesC.w, weightTL, weightTR, weightBL, weightBR };
    int2 offsets[8] = { int2(-1, 0), int2(1, 0), int2(0, -1), int2(0, 1), int2(-1, -1), int2(1, -1), int2(-1, 1),
        int2(1, 1) };
    for (uint i = 0u; i < 8u; i++)
    {
        sum += weights[i] * LoadWorking(constants, pixel + offsets[i], size);
        sumWeight += weights[i];
    }

    float4 term = sum / sumWeight;
    float visibility = saturate(term.w * OcclusionTermScale);

    // The occlusion fades to none at the picture's edges, where the slices see past the picture.
    float2 uv = (float2(pixel) + 0.5) * constants.viewportPixelSize;
    float2 edgeDistance = min(uv, 1.0 - uv) / max(constants.edgeFade, Epsilon);
    visibility = lerp(1.0, visibility, saturate(min(edgeDistance.x, edgeDistance.y)));

    StorageRef occlusion = { constants.occlusion, 0u };
    occlusion.Store(thread.xy, float4(visibility, 0.0, 0.0, 0.0));
    if (constants.bentNormal != 0u)
    {
        float3 viewBent = NormalizeSafe(term.xyz, float3(0.0, 0.0, -1.0));
        float3 worldBent = NormalizeSafe(mul((float3x3)frame.view, viewBent), float3(0.0, 1.0, 0.0));
        StorageRef bent = { constants.bentNormal, 0u };
        bent.Store(thread.xy, float4(EncodeUnitVectorUnorm(worldBent), 0.0, 1.0));
    }
}
