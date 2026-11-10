// The traced shadow mask's noise taken out in place, into the mask shading reads. The sun's slot, the same light at
// every pixel, is denoised in time, then in space: ClassifyShadows skips each 8 by 8 pixels whose neighbourhood the
// sun's rays found all lit or all dark, and blends every other pixel with where it was last frame, clamped to its
// neighbours, keeping a running mean and variance of it; FilterShadows then blurs that twice, 1 and 2 pixels apart,
// and BlendShadows a third time, 4 apart, by how alike neighbours' depths, normals and shadows are, into the mask. A
// tile light's slot may hold another light in the next tile or the next frame, so BlendShadows only blends those with
// last frame.
// All of it runs at the traced size, each traced pixel reading the picture's pixel at its footprint's corner; then
// UpsampleShadows brings the mask and the sun's tint to the picture's size, which shading reads. The pass header's x is
// the frame's buffer, which holds the shadows, y the denoiser's constants and w the filter's pass.
module "DenoiseShadows";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Core.Pack;
import Scene.Lights;
import Scene.Shadows;
import Light.Indirect;

// C#'s ShadowDenoiseConstants: the heap ids of the denoiser's targets and the traced size.
public struct ShadowDenoiseConstants
{
    // A word each 8 by 8 pixels: whether ClassifyShadows skipped them, and whether as all lit.
    public uint tileStates;
    // The sun's shadow and its variance: ClassifyShadows' blend, then the second filter pass's.
    public uint sunBlend;
    // The first filter pass's, which the next frame blends with.
    public uint sunHistory;
    // The sun's mean, its sum of squared differences and how many frames they hold, and the pixel's view depth: this
    // frame's and last frame's.
    public uint moments;
    public uint previousMoments;
    // The traced mask, as the rays left it and then denoised in place, and last frame's.
    public uint mask;
    public uint previousMask;
    // The sun's tint as traced.
    public uint sunTint;
    // Whether last frame's targets hold a frame of this size.
    public uint hasHistory;
    public uint width;
    public uint height;
}

static const uint TileSkipped = 1u;
static const uint TileLit = 2u;

public ShadowDenoiseConstants GetDenoise() { return LoadConstants<ShadowDenoiseConstants>(GetPassHeader().y); }

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
        uint2 texel = GetPicturePixel(frame, min(quad + uint2(i % 2u, i / 2u), size - 1u), size, float2(0.0));
        float depth = depthTexture.Load(texel, 0u).x;
        if (depth > nearest)
        {
            nearest = depth;
            velocity = velocityTexture.Load(texel, 0u).xy;
        }
    }

    return velocity;
}

// The traced pixel at a point of last frame's screen.
uint2 GetHistoryPixel(float2 historyUv, uint2 size) { return min(uint2(historyUv * float2(size)), size - 1u); }

// Whether last frame saw something else there: off its screen, or at a view depth, before, too far from where this
// surface was.
bool IsDisoccluded(float2 historyUv, float before, float viewDepthThen, float3 normal, float3 toEye)
{
    if (any(historyUv <= 0.0) || any(historyUv >= 1.0))
        return true;

    // A surface seen edge-on changes depth fast across a pixel, so it is allowed more.
    float edgeOn = pow(max(1.0 - dot(toEye, normal), 0.0), 8.0);
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
    uint2 size = uint2(denoise.width, denoise.height);
    bool isInside = all(thread.xy < size);
    uint2 pixel = min(thread.xy, size - 1u);
    uint2 picturePixel = GetPicturePixel(frame, pixel, size, float2(0.0));
    TextureRef depthTexture = { frame.depth };
    float depth = depthTexture.Load(picturePixel, 0u).x;
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
    float3 normal = DecodeOctahedral(normalTexture.Load(picturePixel, 0u).xy * 2.0 - 1.0);
    float3 toEye = -normalize(ReconstructRelative(frame, uv, depth));
    float viewDepthThen = velocityTexture.Load(picturePixel, 0u).z;
    bool hasHistory = denoise.hasHistory != 0u;
    float3 previous = float3(0.0);
    if (hasHistory)
    {
        TextureRef previousMoments = { denoise.previousMoments };
        float4 before = previousMoments.Load(GetHistoryPixel(historyUv, size), 0u);
        if (!IsDisoccluded(historyUv, before.w, viewDepthThen, normal, toEye))
            previous = before.xyz;
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
        uint2 picturePixel = GetPicturePixel(frame, source, size, float2(0.0));
        int2 slot = local + offset;
        groupShadows[slot.y][slot.x] = inputTexture.Load(source, 0u).xy;
        groupDepths[slot.y][slot.x] = depthTexture.Load(picturePixel, 0u).x;
        groupNormals[slot.y][slot.x] = PackUnorm2x16(normalTexture.Load(picturePixel, 0u).xy);
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

// The group's tile state: whether ClassifyShadows skipped it, and whether as all lit.
uint LoadTileState(ShadowDenoiseConstants denoise, uint2 group)
{
    ArrayRef<uint> tileStates = { denoise.tileStates };
    return tileStates[GetTileStateIndex(group, denoise.width)];
}

// A filter pass's shadow and variance at a pixel: a skipped group's all lit or all dark, with none.
float2 GetFiltered(FrameConstants frame, uint state, uint input, uint2 thread, uint2 local, int step, uint2 size)
{
    if ((state & TileSkipped) != 0u)
        return float2((state & TileLit) != 0u ? 1.0 : 0.0, 0.0);

    return Filter(frame, input, int2(thread), int2(local), step, size);
}

// The first pass, from ClassifyShadows' blend into what the next frame blends with, and the second, back.
[shader("compute")]
[numthreads(8, 8, 1)]
void FilterShadows(uint3 thread : SV_DispatchThreadID, uint3 local : SV_GroupThreadID, uint3 group : SV_GroupID)
{
    FrameConstants frame = GetFrame();
    ShadowDenoiseConstants denoise = GetDenoise();
    bool isFirst = GetPassHeader().w == 0u;
    uint2 size = uint2(denoise.width, denoise.height);
    uint state = LoadTileState(denoise, group.xy);

    // The second pass leaves a skipped group as ClassifyShadows wrote it.
    if (!isFirst && (state & TileSkipped) != 0u)
        return;

    uint input = isFirst ? denoise.sunBlend : denoise.sunHistory;
    float2 result = GetFiltered(frame, state, input, thread.xy, local.xy, isFirst ? 1 : 2, size);
    if (any(thread.xy >= size))
        return;

    StorageRef target = { isFirst ? denoise.sunHistory : denoise.sunBlend, 0u };
    target.Store(thread.xy, float4(result, 0.0, 0.0));
}

// A blended slot as the 8 bits the mask holds, rounded towards the rays' value, so a pixel that stays still keeps
// moving until it reaches it rather than stopping within a step of it.
float GetStepTowards(float value, float target)
{
    float steps = value * 255.0;
    return (target > value ? ceil(steps) : floor(steps)) / 255.0;
}

// The third filter pass, from the second's, its sun given back some of the contrast the blur took where the variance
// is low, into the mask.
[shader("compute")]
[numthreads(8, 8, 1)]
void BlendShadows(uint3 thread : SV_DispatchThreadID, uint3 local : SV_GroupThreadID, uint3 group : SV_GroupID)
{
    FrameConstants frame = GetFrame();
    ShadowDenoiseConstants denoise = GetDenoise();
    uint2 size = uint2(denoise.width, denoise.height);
    uint state = LoadTileState(denoise, group.xy);
    float2 sun = GetFiltered(frame, state, denoise.sunBlend, thread.xy, local.xy, 4, size);
    if (any(thread.xy >= size))
        return;

    // The sky, which the rays left all lit.
    uint2 picturePixel = GetPicturePixel(frame, thread.xy, size, float2(0.0));
    TextureRef depthTexture = { frame.depth };
    if (depthTexture.Load(picturePixel, 0u).x <= 0.0)
        return;

    float denoised = saturate(pow(abs(sun.x), max(1.2 - sun.y, 1.0)));
    TextureRef velocityTexture = { frame.velocity };
    uint4 traced = LoadShadowMask(denoise.mask, thread.xy, size.x);
    float4 velocity = velocityTexture.Load(picturePixel, 0u);
    float2 historyUv = GetTexelCenterUv(thread.xy, size) + velocity.xy;
    bool isReprojected = denoise.hasHistory != 0u && all(saturate(historyUv) == historyUv);
    uint2 historyPixel = GetHistoryPixel(historyUv, size);
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

    StoreShadowMask(denoise.mask, thread.xy, size.x, EncodeShadow(words, 0u, denoised));
}

// A pixel of the picture's mask and the sun's tint, read from the four traced pixels around it on its surface: the
// sun's slot and tint from each, and a tile light's slot only from those in the pixel's own tile of lights, as a slot
// holds the light its tile lists there; from the traced pixel that holds it where none counts. The sky is all lit.
[shader("compute")]
[numthreads(8, 8, 1)]
void UpsampleShadows(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    Shadows shadows = GetShadows();
    ShadowDenoiseConstants denoise = GetDenoise();
    uint2 size = uint2(frame.width, frame.height);
    if (any(thread.xy >= size))
        return;

    uint2 tracedSize = uint2(denoise.width, denoise.height);
    StorageRef tints = { shadows.sunTint, 0u };
    TextureRef tracedTints = { denoise.sunTint };
    TextureRef depthTexture = { frame.depth };
    float depth = depthTexture.Load(thread.xy, 0u).x;
    if (depth == 0.0)
    {
        StoreShadowMask(shadows.mask, thread.xy, size.x, uint4(0xFFFFFFFFu));
        tints.Store(thread.xy, float4(1.0));
        return;
    }

    TextureRef normalTexture = { frame.gbufferNormal };
    float3 relative = ReconstructRelative(frame, GetTexelCenterUv(thread.xy, size), depth);
    float3 normal = DecodeOctahedral(normalTexture.Load(thread.xy, 0u).xy * 2.0 - 1.0);
    float viewDepth = ToViewDepth(depth, frame.near);
    TracedTaps taps = GetTracedTaps(frame, tracedSize, float2(0.0), thread.xy, relative, normal, viewDepth, 1.0);
    uint2 tile = thread.xy / LightTileSize;
    uint4 words[4];
    float4 tileWeights = float4(0.0);
    float3 tint = float3(0.0);
    [ForceUnroll]
    for (uint i = 0u; i < 4u; i++)
    {
        uint2 texel = taps.GetTexel(i);
        words[i] = LoadShadowMask(denoise.mask, texel, tracedSize.x);
        tint += tracedTints.Load(texel, 0u).rgb * taps.weights[i];
        bool isSameTile = all(GetPicturePixel(frame, texel, tracedSize, float2(0.0)) / LightTileSize == tile);
        tileWeights[i] = isSameTile ? taps.weights[i] : 0.0;
    }

    float sunSum = dot(taps.weights, float4(1.0));
    float tileSum = dot(tileWeights, float4(1.0));
    uint2 nearest = GetTracedTexel(frame, thread.xy, tracedSize);
    uint4 nearestWords = LoadShadowMask(denoise.mask, nearest, tracedSize.x);
    uint4 result = uint4(0u);
    [ForceUnroll]
    for (uint slot = 0u; slot < ShadowSlots; slot++)
    {
        float4 weights = slot == 0u ? taps.weights : tileWeights;
        float sum = slot == 0u ? sunSum : tileSum;
        float4 values = float4(
            GetShadow(words[0], slot), GetShadow(words[1], slot), GetShadow(words[2], slot), GetShadow(words[3], slot));
        float value = sum > 0.0 ? dot(values, weights) / sum : GetShadow(nearestWords, slot);
        result = EncodeShadow(result, slot, value);
    }

    StoreShadowMask(shadows.mask, thread.xy, size.x, result);
    tints.Store(thread.xy, float4(sunSum > 0.0 ? tint / sunSum : tracedTints.Load(nearest, 0u).rgb, 1.0));
}
