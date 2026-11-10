// The traced shadow mask's noise taken out in place, into the mask shading reads. The sun's slot, the same light at
// every pixel, is denoised in time, then in space: ClassifyShadows skips each 8 by 8 pixels whose neighbourhood the
// sun's rays found all lit or all dark, and blends every other pixel with where it was last frame, clamped to its
// neighbours, keeping a running mean and variance of it; FilterShadows then blurs that three times, 1, 2 and 4 pixels
// apart, by how alike neighbours' depths, normals and shadows are. A tile light's slot may hold another light in the
// next tile or the next frame, so BlendShadows only blends those with last frame, and writes the sun's into the mask.
// The pass header's x is the frame's buffer, which holds the shadows, y the denoiser's constants and w the filter's
// pass.
module "DenoiseShadows";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Core.Pack;
import Scene.Lights;
import Scene.Shadows;

// C#'s ShadowDenoiseConstants: the heap ids of the denoiser's targets.
struct ShadowDenoiseConstants
{
    // A word each 8 by 8 pixels: whether ClassifyShadows skipped them, and whether as all lit.
    uint tileStates;
    // The sun's shadow and its variance: ClassifyShadows' blend, then the second filter pass's.
    uint sunBlend;
    // The first filter pass's, which the next frame blends with.
    uint sunHistory;
    // The last filter pass's: the denoised sun.
    uint sunDenoised;
    // The sun's mean, its sum of squared differences and how many frames they hold, and the pixel's view depth: this
    // frame's and last frame's.
    uint moments;
    uint previousMoments;
    // Last frame's mask shading read.
    uint previousMask;
    // Whether last frame's targets hold a frame of this size.
    uint hasHistory;
}

static const uint TileSkipped = 1u;
static const uint TileLit = 2u;

ShadowDenoiseConstants GetDenoise() { return LoadConstants<ShadowDenoiseConstants>(GetPassHeader().y); }

uint GetTileStateIndex(uint2 group, uint width) { return group.y * ((width + 7u) / 8u) + group.x; }

// The weight of a pixel i away in a 17-pixel Gaussian, over the sum of all 17.
float GetKernelWeight(int i) { return exp(-3.0 * float(i * i) / 81.0) * 0.1108293; }

groupshared uint groupAnyFalse;
groupshared float groupRowMoments[8][24];

// Whether a value is true at every thread of the group; every thread of it must ask.
bool IsTrueForGroup(bool value)
{
    GroupMemoryBarrierWithGroupSync();
    groupAnyFalse = 0u;
    GroupMemoryBarrierWithGroupSync();
    if (!value)
        groupAnyFalse = 1u;

    GroupMemoryBarrierWithGroupSync();
    return groupAnyFalse == 0u;
}

// Whether the sun's rays reached it from every pixel, or from none, as far as the filter passes reach around a group.
void SearchRegion(Shadows shadows, uint2 group, uint2 size, out bool isAllLit, out bool isAllDark)
{
    int2 tiles = int2((size + SunTileSize - 1u) / SunTileSize);
    int2 baseTile = int2(group * 8u / SunTileSize);
    ArrayRef<uint> sunTiles = { shadows.sunTiles };
    uint anyLit = 0u;
    uint everyLit = 0xFFFFFFFFu;
    for (int y = -2; y <= 3; y++)
    {
        for (int x = -1; x <= 1; x++)
        {
            uint2 tile = uint2(clamp(baseTile + int2(x, y), int2(0), tiles - 1));
            uint bits = sunTiles[GetSunTileIndex(tile, size.x)];
            anyLit |= bits;
            everyLit &= bits;
        }
    }

    isAllLit = everyLit == 0xFFFFFFFFu;
    isAllDark = anyLit == 0u;
}

// The sun's lit share of the 17 pixels of a row centred on a pixel, Gaussian-weighted.
float GetRowMoment(Shadows shadows, int2 pixel, uint2 size)
{
    if (pixel.y < 0 || pixel.y >= int(size.y))
        return 0.0;

    ArrayRef<uint> sunTiles = { shadows.sunTiles };
    uint2 tile = uint2(pixel) / SunTileSize;
    uint tilesAcross = (size.x + SunTileSize.x - 1u) / SunTileSize.x;
    uint index = GetSunTileIndex(tile, size.x);
    uint left = tile.x > 0u ? sunTiles[index - 1u] : 0u;
    uint right = tile.x + 1u < tilesAcross ? sunTiles[index + 1u] : 0u;
    uint row = (uint(pixel.y) % SunTileSize.y) * SunTileSize.x;
    uint bits = ((left >> row) & 0xFFu) | (((sunTiles[index] >> row) & 0xFFu) << 8u) | (((right >> row) & 0xFFu) << 16u);
    bits >>= uint(pixel.x) % SunTileSize.x;

    float moment = 0.0;
    for (int i = 0; i <= 16; i++)
    {
        if (((bits >> uint(i)) & 1u) != 0u)
            moment += GetKernelWeight(abs(i - 8));
    }

    return moment;
}

// The sun's lit share of the 17 by 17 pixels around a pixel, Gaussian-weighted: its mean, and its variance too, as a
// pixel holds a single 0 or 1.
float GetNeighbourhood(Shadows shadows, int2 pixel, uint2 local, uint2 size)
{
    float upper = GetRowMoment(shadows, pixel - int2(0, 8), size);
    float center = GetRowMoment(shadows, pixel, size);
    float lower = GetRowMoment(shadows, pixel + int2(0, 8), size);
    groupRowMoments[local.x][local.y] = upper;
    groupRowMoments[local.x][local.y + 8u] = center;
    groupRowMoments[local.x][local.y + 16u] = lower;
    GroupMemoryBarrierWithGroupSync();

    float moment = center * GetKernelWeight(0) + (upper + lower) * GetKernelWeight(8);
    for (int i = 1; i < 8; i++)
    {
        float above = groupRowMoments[local.x][8u + local.y - uint(i)];
        float below = groupRowMoments[local.x][8u + local.y + uint(i)];
        moment += (above + below) * GetKernelWeight(i);
    }

    return moment;
}

// The move to last frame's screen of the nearest of the pixel's 2 by 2, so an edge reprojects with what is in front.
float2 GetNearestVelocity(FrameConstants frame, uint2 pixel, uint2 size)
{
    TextureRef depthTexture = { frame.depth };
    TextureRef velocityTexture = { frame.velocity };
    uint2 quad = pixel & ~1u;
    float nearest = -1.0;
    float2 velocity = float2(0.0);
    for (uint i = 0u; i < 4u; i++)
    {
        uint2 texel = min(quad + uint2(i % 2u, i / 2u), size - 1u);
        float depth = depthTexture.Load(texel, 0u).x;
        if (depth > nearest)
        {
            nearest = depth;
            velocity = velocityTexture.Load(texel, 0u).xy;
        }
    }

    return velocity;
}

// Whether last frame saw something else there: off its screen, or at a depth too far from where this surface was.
bool IsDisoccluded(ShadowDenoiseConstants denoise, float2 historyUv, uint2 size, float3 normal, float3 toEye, float viewDepthThen)
{
    if (any(historyUv <= 0.0) || any(historyUv >= 1.0))
        return true;

    // A surface seen edge-on changes depth fast across a pixel, so it is allowed more.
    float edgeOn = pow(max(1.0 - dot(toEye, normal), 0.0), 8.0);
    TextureRef previousMoments = { denoise.previousMoments };
    float before = previousMoments.Load(min(uint2(historyUv * float2(size)), size - 1u), 0u).w;
    float difference = abs(before - viewDepthThen) / max(viewDepthThen, Epsilon);
    return difference >= lerp(1e-2, 1e-1, edgeOn);
}

[shader("compute")]
[numthreads(8, 8, 1)]
void ClassifyShadows(uint3 thread : SV_DispatchThreadID, uint3 local : SV_GroupThreadID, uint3 group : SV_GroupID)
{
    FrameConstants frame = GetFrame();
    Shadows shadows = GetShadows();
    ShadowDenoiseConstants denoise = GetDenoise();
    uint2 size = uint2(frame.width, frame.height);
    bool isInside = all(thread.xy < size);
    uint2 pixel = min(thread.xy, size - 1u);
    TextureRef depthTexture = { frame.depth };
    float depth = depthTexture.Load(pixel, 0u).x;
    bool isReceiver = isInside && depth > 0.0;
    float viewDepth = isReceiver ? ToViewDepth(depth, frame.near) : 0.0;
    ArrayRef<uint> tileStates = { denoise.tileStates };
    uint stateIndex = GetTileStateIndex(group.xy, size.x);
    StorageRef blend = { denoise.sunBlend, 0u };
    StorageRef moments = { denoise.moments, 0u };

    // A group of sky, or one whose neighbourhood is all lit or all dark, is skipped, with values its neighbours can read.
    bool isAllLit = false;
    bool isAllDark = true;
    bool isSky = IsTrueForGroup(!isReceiver);
    if (!isSky)
        SearchRegion(shadows, group.xy, size, isAllLit, isAllDark);

    if (isSky || IsTrueForGroup(isAllLit || isAllDark))
    {
        float value = isAllLit ? 1.0 : 0.0;
        if (all(local.xy == 0u))
            tileStates[stateIndex] = TileSkipped | (isAllLit ? TileLit : 0u);

        if (isInside)
        {
            blend.Store(thread.xy, float4(value, 0.0, 0.0, 0.0));
            moments.Store(thread.xy, float4(value, 0.0, isReceiver ? 1.0 : 0.0, viewDepth));
        }

        return;
    }

    if (all(local.xy == 0u))
        tileStates[stateIndex] = 0u;

    float2 velocity = GetNearestVelocity(frame, pixel, size);
    float neighbourhood = GetNeighbourhood(shadows, int2(thread.xy), local.xy, size);
    if (!isInside)
        return;

    if (!isReceiver)
    {
        blend.Store(thread.xy, float4(0.0));
        moments.Store(thread.xy, float4(0.0));
        return;
    }

    float2 uv = GetTexelCenterUv(pixel, size);
    float2 historyUv = uv + velocity;
    ArrayRef<uint> sunTiles = { shadows.sunTiles };
    uint tile = sunTiles[GetSunTileIndex(pixel / SunTileSize, size.x)];
    float current = (tile & GetSunTileBit(pixel)) != 0u ? 1.0 : 0.0;

    // The running mean and variance of the sun's rays here, restarted where last frame saw something else.
    TextureRef normalTexture = { frame.gbufferNormal };
    TextureRef velocityTexture = { frame.velocity };
    float3 normal = DecodeOctahedral(normalTexture.Load(pixel, 0u).xy * 2.0 - 1.0);
    float3 toEye = -normalize(ReconstructRelative(frame, uv, depth));
    float viewDepthThen = velocityTexture.Load(pixel, 0u).z;
    bool hasHistory = denoise.hasHistory != 0u;
    float3 previous = float3(0.0);
    if (hasHistory && !IsDisoccluded(denoise, historyUv, size, normal, toEye, viewDepthThen))
    {
        TextureRef previousMoments = { denoise.previousMoments };
        previous = previousMoments.Load(min(uint2(historyUv * float2(size)), size - 1u), 0u).xyz;
    }

    float count = previous.z + 1.0;
    float mean = previous.x + (current - previous.x) / count;
    float squares = previous.y + (current - previous.x) * (current - mean);
    float variance = count > 1.0 ? squares / (count - 1.0) : 1.0;
    float3 running = float3(mean, squares, count);

    // Last frame's shadow clamped to the neighbourhood, and trusted less the further it stood outside it.
    float spatialVariance = max(neighbourhood - neighbourhood * neighbourhood, 0.0);
    float deviation = sqrt(spatialVariance);
    float shadowBefore = current;
    if (hasHistory)
    {
        TextureRef history = { denoise.sunHistory };
        SamplerRef sampler = { GetLights().lutSampler };
        shadowBefore = history.SampleLevel(sampler, historyUv, 0.0).x;
    }

    float clamped = clamp(shadowBefore, neighbourhood - 0.5 * deviation, neighbourhood + 0.5 * deviation);
    float discontinuity = (shadowBefore - neighbourhood) / max(0.5 * deviation, 0.001);
    running.z *= exp(-discontinuity * discontinuity / 20.0);

    // Few frames say little, so the filter is told the variance is larger.
    if (running.z < 16.0)
        variance = max(variance, spatialVariance) * max(16.0 - running.z, 1.0);

    float historyWeight = sqrt(max(8.0 - running.z, 0.0) / 8.0);
    float shadow = lerp(clamped, current, lerp(0.05, 1.0, historyWeight));
    blend.Store(thread.xy, float4(shadow, variance, 0.0, 0.0));
    moments.Store(thread.xy, float4(running, viewDepth));
}

groupshared float2 groupShadows[16][16];
groupshared float groupDepths[16][16];
groupshared uint groupNormals[16][16];

// A pixel's shadow and variance blurred with its 3 by 3 neighbours a step apart, each weighted by how alike their
// shadows are against the local deviation, how near their depths are and how alike their normals.
float2 Filter(FrameConstants frame, uint input, int2 pixel, int2 local, int step, uint2 size)
{
    // The group's 8 by 8 and 4 pixels around it, as far as a step of 4 reaches.
    TextureRef inputTexture = { input };
    TextureRef depthTexture = { frame.depth };
    TextureRef normalTexture = { frame.gbufferNormal };
    for (uint i = 0u; i < 4u; i++)
    {
        int2 offset = int2(int(i % 2u), int(i / 2u)) * 8;
        uint2 source = uint2(clamp(pixel - 4 + offset, int2(0), int2(size) - 1));
        int2 slot = local + offset;
        groupShadows[slot.y][slot.x] = inputTexture.Load(source, 0u).xy;
        groupDepths[slot.y][slot.x] = depthTexture.Load(source, 0u).x;
        groupNormals[slot.y][slot.x] = PackUnorm2x16(normalTexture.Load(source, 0u).xy);
    }

    GroupMemoryBarrierWithGroupSync();
    int2 center = local + 4;
    float depth = groupDepths[center.y][center.x];
    if (depth <= 0.0)
        return float2(0.0);

    float2 centerShadow = groupShadows[center.y][center.x];
    float3 centerNormal = DecodeOctahedral(UnpackUnorm2x16(groupNormals[center.y][center.x]) * 2.0 - 1.0);
    float centerDepth = ToViewDepth(depth, frame.near);
    float variance = 0.0;
    for (int y = -1; y <= 1; y++)
    {
        for (int x = -1; x <= 1; x++)
        {
            float weight = 0.25 * (x == 0 ? 1.0 : 0.5) * (y == 0 ? 1.0 : 0.5);
            variance += weight * groupShadows[center.y + y][center.x + x].y;
        }
    }

    float deviation = sqrt(max(variance + 1e-9, 0.0));
    float kernel[3] = { 1.0, 2.0 / 3.0, 1.0 / 6.0 };
    float weightSum = 1.0;
    float2 shadowSum = centerShadow;
    for (int y = -1; y <= 1; y++)
    {
        for (int x = -1; x <= 1; x++)
        {
            int2 at = center + int2(x, y) * step;
            float neighbourDepth = groupDepths[at.y][at.x];
            if ((x == 0 && y == 0) || neighbourDepth <= 0.0)
                continue;

            float2 shadow = groupShadows[at.y][at.x];
            float3 normal = DecodeOctahedral(UnpackUnorm2x16(groupNormals[at.y][at.x]) * 2.0 - 1.0);
            float weight = kernel[abs(x)] * kernel[abs(y)];
            weight *= exp(-abs(centerShadow.x - shadow.x) / deviation);
            weight *= exp(-abs(centerDepth - ToViewDepth(neighbourDepth, frame.near)));
            weight *= pow(saturate(dot(centerNormal, normal)), 32.0);
            shadowSum += float2(weight, weight * weight) * shadow;
            weightSum += weight;
        }
    }

    return float2(shadowSum.x / weightSum, shadowSum.y / (weightSum * weightSum));
}

[shader("compute")]
[numthreads(8, 8, 1)]
void FilterShadows(uint3 thread : SV_DispatchThreadID, uint3 local : SV_GroupThreadID, uint3 group : SV_GroupID)
{
    FrameConstants frame = GetFrame();
    ShadowDenoiseConstants denoise = GetDenoise();
    uint pass = GetPassHeader().w;
    uint2 size = uint2(frame.width, frame.height);
    bool isInside = all(thread.xy < size);
    ArrayRef<uint> tileStates = { denoise.tileStates };
    uint state = tileStates[GetTileStateIndex(group.xy, size.x)];
    uint input = pass == 1u ? denoise.sunHistory : denoise.sunBlend;
    uint output = pass == 0u ? denoise.sunHistory : (pass == 1u ? denoise.sunBlend : denoise.sunDenoised);

    // A skipped group is all lit or all dark, which the second pass leaves as ClassifyShadows wrote it.
    float2 result;
    if ((state & TileSkipped) != 0u)
    {
        if (pass == 1u || !isInside)
            return;

        result = float2((state & TileLit) != 0u ? 1.0 : 0.0, 0.0);
    }
    else
    {
        result = Filter(frame, input, int2(thread.xy), int2(local.xy), 1 << int(pass), size);
        if (!isInside)
            return;
    }

    StorageRef target = { output, 0u };
    if (pass < 2u)
    {
        target.Store(thread.xy, float4(result, 0.0, 0.0));
        return;
    }

    // Some of the contrast the blur took, given back where the variance is low.
    float contrast = max(1.2 - result.y, 1.0);
    target.Store(thread.xy, float4(saturate(pow(abs(result.x), contrast)), 0.0, 0.0, 0.0));
}

// A blended slot as the 8 bits the mask holds, rounded towards the rays' value, so a pixel that stays still keeps
// moving until it reaches it rather than stopping within a step of it.
float GetStepTowards(float value, float target)
{
    float steps = value * 255.0;
    return (target > value ? ceil(steps) : floor(steps)) / 255.0;
}

[shader("compute")]
[numthreads(8, 8, 1)]
void BlendShadows(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    Shadows shadows = GetShadows();
    ShadowDenoiseConstants denoise = GetDenoise();
    uint2 size = uint2(frame.width, frame.height);
    if (any(thread.xy >= size))
        return;

    // The sky, which the rays left all lit.
    TextureRef depthTexture = { frame.depth };
    if (depthTexture.Load(thread.xy, 0u).x <= 0.0)
        return;

    TextureRef denoised = { denoise.sunDenoised };
    TextureRef velocityTexture = { frame.velocity };
    uint4 traced = LoadShadowMask(shadows.mask, thread.xy, size.x);
    float4 velocity = velocityTexture.Load(thread.xy, 0u);
    float2 historyUv = GetTexelCenterUv(thread.xy, size) + velocity.xy;
    bool isReprojected = denoise.hasHistory != 0u && all(saturate(historyUv) == historyUv);
    uint2 historyPixel = min(uint2(historyUv * float2(size)), size - 1u);
    if (isReprojected)
    {
        TextureRef previousMoments = { denoise.previousMoments };
        isReprojected = abs(previousMoments.Load(historyPixel, 0u).w - velocity.z) <= 1.0;
    }

    // Where last frame saw something else, the rays as they are; elsewhere each slot blended with last frame's, the
    // more the less it changed and the less the pixel moved.
    uint4 words = traced & uint4(~0xFFu, 0xFFFFFFFFu, 0xFFFFFFFFu, 0xFFFFFFFFu);
    if (isReprojected)
    {
        uint4 previousWords = LoadShadowMask(denoise.previousMask, historyPixel, size.x);
        float motion = saturate(length(velocity.xy * float2(size)) / 100.0);
        words = uint4(0u);
        for (uint slot = 1u; slot < ShadowSlots; slot++)
        {
            float current = GetShadow(traced, slot);
            float previous = GetShadow(previousWords, slot);
            float change = abs(current - previous) / max(current, max(previous, 0.2));
            float blend = lerp(lerp(0.88, 1.0, (1.0 - change) * (1.0 - change)), 0.2, motion);
            words = EncodeShadow(words, slot, GetStepTowards(lerp(current, previous, blend), current));
        }
    }

    StoreShadowMask(shadows.mask, thread.xy, size.x, EncodeShadow(words, 0u, denoised.Load(thread.xy, 0u).x));
}
