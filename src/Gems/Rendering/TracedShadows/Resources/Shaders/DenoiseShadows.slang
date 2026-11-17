// The traced shadow mask's noise taken out in place, into the mask shading reads. The sun's slot, the same light at
// every pixel, is denoised in time, then in space: ClassifyShadows skips each 8 by 8 pixels whose neighbourhood the
// sun's rays found all lit or all dark, and blends every other pixel with where it was last frame, clamped to its
// neighbours, keeping a running mean and variance of it. FilterShadows then blurs that once or twice, 1 and 2 pixels
// apart, and FilterLastAndBlendShadows a last time, 4 apart, by how alike neighbours' depths, normals and shadows are,
// into the mask. The next frame blends with ClassifyShadows' blend, not with a blurred one: what little a blur takes
// from across an edge would stay in the history and grow each frame. A tile light's slot holds the light its tile lists there, so FilterLastAndBlendShadows blends each with
// the slot that held the same light last frame, found in last frame's tiles of lights, which KeepLightTiles keeps.
// FilterTileShadows then blurs those slots once over the pixel's own tile of lights, into the filtered mask that the next
// frame blends with.
// All of it runs at the traced size, each traced pixel reading the picture's pixel at its footprint's corner. Then
// UpsampleShadows brings the filtered mask and the sun's tint to the picture's size, which shading reads. The pass header's x is
// the frame's buffer, which holds the shadows, y the denoiser's constants and w the filter's pass, or for
// FilterLastAndBlendShadows the target the last filter pass wrote.
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
    // The sun's tiles: a bit for each traced pixel of 8 by 4 that its ray reached the sun from, a word a tile, row by
    // row.
    public uint sunTiles;
    // How far towards the sun the nearest thing in a traced pixel's ray's way is, 0 with nothing in it.
    public uint sunOccluder;
    // The sun's shadow and its variance as ClassifyShadows blends it, which the next frame blends with.
    public uint sunBlend;
    // Last frame's blend, then the second filter pass's.
    public uint sunHistory;
    // The first filter pass's.
    public uint sunFiltered;
    // How many frames the sun's shadow holds and the pixel's view depth: this frame's and last frame's.
    public uint moments;
    public uint previousMoments;
    // The traced mask, as the rays left it and then blended in place, and the filtered mask: this frame's and last
    // frame's.
    public uint mask;
    public uint filteredMask;
    public uint previousMask;
    // Last frame's tiles of lights, a copy of the frame's.
    public uint lastLightTiles;
    // The sun's tint as traced. 0 while no glass tints it.
    public uint sunTint;
    // Whether last frame's targets hold a frame of this size.
    public uint hasHistory;
    public uint width;
    public uint height;
    // The blue noise the rays pick their points on the lights with.
    public uint blueNoise;
}

public static const uint2 SunTileSize = uint2(8u, 4u);

public uint GetSunTileIndex(uint2 tile, uint width)
{
    return tile.y * ((width + SunTileSize.x - 1u) / SunTileSize.x) + tile.x;
}

public uint GetSunTileBit(uint2 pixel)
{
    return 1u << ((pixel.y % SunTileSize.y) * SunTileSize.x + pixel.x % SunTileSize.x);
}

// The bits of a pixel's words that hold the tile lights' slots, all but the sun's.
static const uint4 TileSlotBits = uint4(~0xFFu, 0xFFFFFFFFu, 0xFFFFFFFFu, 0xFFFFFFFFu);

// The frame count at which a pixel's shadow takes the least of each frame's rays.
static const float SettledCount = 8.0;

static const uint TileSkipped = 1u;
static const uint TileLit = 2u;

public ShadowDenoiseConstants GetDenoise() { return LoadConstants<ShadowDenoiseConstants>(GetPassHeader().y); }

public uint GetTileStateIndex(uint2 group, uint width) { return group.y * ((width + 7u) / 8u) + group.x; }

// The weight of a pixel i away in a 17-pixel Gaussian, over the sum of all 17.
float GetKernelWeight(int i) { return exp(-3.0 * float(i * i) / 81.0) * 0.1108293; }

groupshared uint groupAnyFalse;
groupshared float groupRowMoments[8][24];

// Whether a value is true at every thread of the group. Every thread of it must ask.
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

// TileSkipped | TileLit, TileSkipped, or 0: whether the sun's rays reached it from every pixel, or from none, as far as
// the filter passes reach around a group.
uint GetRegionState(ArrayRef<uint> sunTiles, uint2 group, uint2 size)
{
    int2 tiles = int2((size + SunTileSize - 1u) / SunTileSize);
    int2 baseTile = int2(group * 8u / SunTileSize);
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

    if (everyLit == 0xFFFFFFFFu)
        return TileSkipped | TileLit;

    return anyLit == 0u ? TileSkipped : 0u;
}

// The sun's lit share of the 17 pixels of a row centred on a pixel, Gaussian-weighted.
float GetRowMoment(ArrayRef<uint> sunTiles, int2 pixel, uint2 size)
{
    if (pixel.y < 0 || pixel.y >= int(size.y))
        return 0.0;

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
float GetNeighbourhood(ArrayRef<uint> sunTiles, int2 pixel, uint2 local, uint2 size)
{
    float upper = GetRowMoment(sunTiles, pixel - int2(0, 8), size);
    float center = GetRowMoment(sunTiles, pixel, size);
    float lower = GetRowMoment(sunTiles, pixel + int2(0, 8), size);
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

// The sun's lit share of the 3 by 3 rays around a pixel.
float GetLocalMean(ArrayRef<uint> sunTiles, int2 pixel, uint2 size)
{
    float lit = 0.0;
    for (int y = -1; y <= 1; y++)
    {
        for (int x = -1; x <= 1; x++)
        {
            uint2 at = uint2(clamp(pixel + int2(x, y), int2(0), int2(size) - 1));
            lit += (sunTiles[GetSunTileIndex(at / SunTileSize, size.x)] & GetSunTileBit(at)) != 0u ? 1.0 : 0.0;
        }
    }

    return lit / 9.0;
}

// The largest change of a pixel's view depth, as a part of it, at which last frame saw the same surface. A surface seen
// edge-on changes depth fast across a pixel, so it is allowed more.
float GetHistoryTolerance(FrameConstants frame, uint2 picturePixel, float2 uv, float depth)
{
    float3 normal = LoadNormal(frame.gbufferNormal, picturePixel);
    float3 toEye = -normalize(ReconstructRelative(frame, uv, depth));
    return lerp(1e-2, 1e-1, pow(max(1.0 - dot(toEye, normal), 0.0), 8.0));
}

// Whether last frame's pixel saw the same surface: its view depth near where the pixel was then.
bool IsSameSurface(float viewDepthBefore, float viewDepthThen, float tolerance)
{
    return abs(viewDepthBefore - viewDepthThen) < tolerance * max(viewDepthThen, Epsilon);
}

[shader("compute")]
[numthreads(8, 8, 1)]
void ClassifyShadows(uint3 thread : SV_DispatchThreadID, uint3 local : SV_GroupThreadID, uint3 group : SV_GroupID)
{
    FrameConstants frame = GetFrame();
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
    ArrayRef<uint> sunTiles = { denoise.sunTiles };

    // A group of sky, or one whose neighbourhood is all lit or all dark, is skipped, with values its neighbours can
    // read. Its receivers count as settled, so a group that the next frame filters starts from its clean shadow rather
    // than from the rays alone.
    uint state = IsTrueForGroup(!isReceiver) ? TileSkipped : GetRegionState(sunTiles, group.xy, size);
    if (all(local.xy == 0u))
        tileStates[stateIndex] = state;

    if ((state & TileSkipped) != 0u)
    {
        if (isInside)
        {
            blend.Store(thread.xy, float4((state & TileLit) != 0u ? 1.0 : 0.0, 0.0, 0.0, 0.0));
            moments.Store(thread.xy, float4(isReceiver ? SettledCount : 0.0, viewDepth, 0.0, 0.0));
        }

        return;
    }

    // The move to last frame's screen of the nearest of the pixel's 2 by 2, so an edge reprojects with what is in
    // front.
    TextureRef velocityTexture = { frame.velocity };
    uint2 quad = pixel & ~1u;
    float nearest = -1.0;
    float2 velocity = float2(0.0);
    for (uint i = 0u; i < 4u; i++)
    {
        uint2 texel = GetPicturePixel(frame, min(quad + uint2(i % 2u, i / 2u), size - 1u), size, float2(0.0));
        float texelDepth = depthTexture.Load(texel, 0u).x;
        if (texelDepth > nearest)
        {
            nearest = texelDepth;
            velocity = velocityTexture.Load(texel, 0u).xy;
        }
    }

    float neighbourhood = GetNeighbourhood(sunTiles, int2(thread.xy), local.xy, size);
    if (!isInside)
        return;

    if (!isReceiver)
    {
        blend.Store(thread.xy, float4(0.0));
        moments.Store(thread.xy, float4(0.0));
        return;
    }

    float2 uv = GetTexelCenterUv(pixel, size);
    // Where the pixel was on last frame's screen, by its velocity alone: on a still view, the history's own pixel. The
    // jitter's move is left to the upscaling, since a read between pixels each frame spreads the shadow's edge wider
    // every frame, and lit ground into the shadow beside its caster.
    float2 historyUv = uv + velocity;
    float current = GetShadow(LoadShadowMask(denoise.mask, pixel, size.x), 0u);

    // How many frames the sun's rays here were seen for, and last frame's shadow: a bilinear read of only those of
    // last frame's pixels that saw the same surface, so an occluder's shadow in front does not bleed onto the surface
    // behind it. Where none did, the count restarts.
    float viewDepthThen = velocityTexture.Load(picturePixel, 0u).z;
    float count = 1.0;
    float shadowBefore = current;
    if (denoise.hasHistory != 0u && IsOnScreen(historyUv))
    {
        float tolerance = GetHistoryTolerance(frame, picturePixel, uv, depth);
        TextureRef previousMoments = { denoise.previousMoments };
        TextureRef history = { denoise.sunHistory };
        float2 position = historyUv * float2(size) - 0.5;
        int2 corner = int2(floor(position));
        float2 fraction = position - float2(corner);

        // The weight, the count and the shadow of the taps that saw the same surface, summed.
        float3 sum = float3(0.0);
        for (uint i = 0u; i < 4u; i++)
        {
            int2 offset = int2(int(i % 2u), int(i / 2u));
            uint2 texel = uint2(clamp(corner + offset, int2(0), int2(size) - 1));
            float2 before = previousMoments.Load(texel, 0u).xy;
            float2 bilinear = lerp(1.0 - fraction, fraction, float2(offset));
            float weight = IsSameSurface(before.y, viewDepthThen, tolerance) ? bilinear.x * bilinear.y : 0.0;
            sum += weight * float3(1.0, before.x, history.Load(texel, 0u).x);
        }

        if (sum.x > 1e-3)
        {
            count += sum.y / sum.x;
            shadowBefore = sum.z / sum.x;
        }
    }

    // A ray is lit or not, so the variance of the rays here is their mean times its complement, the mean last frame's
    // shadow with this ray added. It is at most 1, as with one frame: a count just above 1 would make it larger than the
    // blend's 16-bit float holds, and the filter would spread the infinity as NaN, drawn dark.
    float mean = lerp(shadowBefore, current, 1.0 / count);
    float variance = count > 1.0 ? min(mean * (1.0 - mean) * count / (count - 1.0), 1.0) : 1.0;

    // Last frame's shadow clamped to the neighbourhood's mean plus or minus its deviation. The neighbourhood holds
    // every surface around, so the range also takes in this frame's ray and the 3 by 3 rays around it: a shadowed
    // floor beside a sunlit face keeps its own dark rather than the face's light. A penumbra differs from its
    // neighbourhood by nature, so the count falls only by how far last frame's shadow stood outside that range, where
    // the shadow truly changed, and slowly, so an edge that a moving sun shifts keeps most of its frames. It stops at 32
    // frames, which the moments' 16-bit floats keep precise.
    float spatialVariance = max(neighbourhood - neighbourhood * neighbourhood, 0.0);
    float deviation = sqrt(spatialVariance);
    float localMean = GetLocalMean(sunTiles, int2(pixel), size);
    float lowest = min(neighbourhood - deviation, min(localMean, current));
    float highest = max(neighbourhood + deviation, max(localMean, current));
    float clamped = clamp(shadowBefore, lowest, highest);
    float outside = (shadowBefore - clamped) / max(0.5 * deviation, 0.05);
    count = min(count * exp(-outside * outside / 20.0), 32.0);

    // Few frames say little, so the filter is told the variance is larger.
    if (count < 16.0)
        variance = max(variance, spatialVariance) * max(16.0 - count, 1.0);

    float historyWeight = sqrt(max(SettledCount - count, 0.0) / SettledCount);
    float shadow = lerp(clamped, current, lerp(0.05, 1.0, historyWeight));
    blend.Store(thread.xy, float4(shadow, variance, 0.0, 0.0));
    moments.Store(thread.xy, float4(count, viewDepth, 0.0, 0.0));
}

groupshared float2 groupShadows[16][16];
groupshared float groupDepths[16][16];
groupshared uint groupNormals[16][16];
groupshared float groupOccluders[16][16];

// How fast the filter's weight falls with a neighbour's distance from the pixel's plane, as a part of its view depth:
// a hundredth of it weighs a third.
static const float FilterPlaneSharpness = 10000.0;

// A pixel's shadow and variance blurred with its 3 by 3 neighbours a step apart, each weighted by how alike their
// shadows are against the local deviation, how near they are to the pixel's plane, how alike their normals, and how
// far they are against the width of the shadow's soft edge there: the sun's angular size times how far the nearest
// thing in the way of the rays around is, so the shadow stays sharp where it meets its caster and softens away from
// it. A skipped group's all lit or all dark, with none.
float2 Filter(
    FrameConstants frame, ShadowDenoiseConstants denoise, uint state, uint input, int2 pixel, int2 local, int step)
{
    uint2 size = uint2(denoise.width, denoise.height);
    if ((state & TileSkipped) != 0u)
        return float2((state & TileLit) != 0u ? 1.0 : 0.0, 0.0);

    // The group's 8 by 8 and 4 pixels around it, as far as a step of 4 reaches.
    TextureRef inputTexture = { input };
    TextureRef depthTexture = { frame.depth };
    TextureRef normalTexture = { frame.gbufferNormal };
    TextureRef occluderTexture = { denoise.sunOccluder };
    for (uint i = 0u; i < 4u; i++)
    {
        int2 offset = int2(int(i % 2u), int(i / 2u)) * 8;
        uint2 source = uint2(clamp(pixel - 4 + offset, int2(0), int2(size) - 1));
        uint2 picturePixel = GetPicturePixel(frame, source, size, float2(0.0));
        int2 slot = local + offset;
        groupShadows[slot.y][slot.x] = inputTexture.Load(source, 0u).xy;
        groupDepths[slot.y][slot.x] = depthTexture.Load(picturePixel, 0u).x;
        groupNormals[slot.y][slot.x] = PackUnorm2x16(normalTexture.Load(picturePixel, 0u).xy);
        groupOccluders[slot.y][slot.x] = occluderTexture.Load(source, 0u).x;
    }

    GroupMemoryBarrierWithGroupSync();
    int2 center = local + 4;
    float depth = groupDepths[center.y][center.x];
    if (depth <= 0.0)
        return float2(0.0);

    float2 centerShadow = groupShadows[center.y][center.x];
    float3 centerNormal = DecodeUnitVector(groupNormals[center.y][center.x]);
    float centerDepth = ToViewDepth(depth, frame.near);
    float3 centerRelative = ReconstructPixel(frame, GetPicturePixel(frame, uint2(pixel), size, float2(0.0)), depth);
    float variance = 0.0;
    for (int y = -1; y <= 1; y++)
    {
        for (int x = -1; x <= 1; x++)
        {
            float weight = 0.25 * (x == 0 ? 1.0 : 0.5) * (y == 0 ? 1.0 : 0.5);
            variance += weight * groupShadows[center.y + y][center.x + x].y;
        }
    }

    // The nearest thing in the way of the rays within the filter's reach, and the soft edge's width in traced pixels.
    // With nothing in their way, or at its foot, the pixel stays as it is.
    float occluder = 1e30;
    for (int y = -step; y <= step; y++)
    {
        for (int x = -step; x <= step; x++)
        {
            float neighbourOccluder = groupOccluders[center.y + y][center.x + x];
            if (neighbourOccluder > 0.0)
                occluder = min(occluder, neighbourOccluder);
        }
    }

    float tracedPixelsPerRadian = frame.pixelsPerRadian * float(size.x) / float(frame.width);
    float softWidth = occluder * 2.0 * SunAngularRadius / centerDepth * tracedPixelsPerRadian;
    if (occluder > 1e29 || softWidth < 0.25)
        return centerShadow;

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
            float3 normal = DecodeUnitVector(groupNormals[at.y][at.x]);
            float weight = kernel[abs(x)] * kernel[abs(y)];
            weight *= exp(-2.0 * float((x * x + y * y) * step * step) / Pow2(softWidth));
            weight *= exp(-abs(centerShadow.x - shadow.x) / deviation);
            uint2 neighbour = uint2(clamp(pixel + int2(x, y) * step, int2(0), int2(size) - 1));
            float3 gap = ReconstructPixel(frame, GetPicturePixel(frame, neighbour, size, float2(0.0)), neighbourDepth)
                - centerRelative;
            weight *= exp(-Pow2(dot(gap, centerNormal) / centerDepth) * FilterPlaneSharpness);
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

// The first pass, from ClassifyShadows' blend, and the second, from the first's into last frame's blend, which
// ClassifyShadows has read by then.
[shader("compute")]
[numthreads(8, 8, 1)]
void FilterShadows(uint3 thread : SV_DispatchThreadID, uint3 local : SV_GroupThreadID, uint3 group : SV_GroupID)
{
    FrameConstants frame = GetFrame();
    ShadowDenoiseConstants denoise = GetDenoise();
    bool isFirst = GetPassHeader().w == 0u;
    uint2 size = uint2(denoise.width, denoise.height);
    uint state = LoadTileState(denoise, group.xy);
    uint input = isFirst ? denoise.sunBlend : denoise.sunFiltered;
    float2 result = Filter(frame, denoise, state, input, int2(thread.xy), int2(local.xy), isFirst ? 1 : 2);
    if (any(thread.xy >= size))
        return;

    StorageRef target = { isFirst ? denoise.sunFiltered : denoise.sunHistory, 0u };
    target.Store(thread.xy, float4(result, 0.0, 0.0));
}

// Whether a tile of lights, at its first word in the tile table, lists any of the frame's lights.
bool ListsAnyLight(ArrayRef<uint> tiles, uint tile, uint buckets)
{
    uint bits = 0u;
    for (uint bucket = 0u; bucket < buckets; bucket++)
        bits |= tiles[tile + bucket];

    return bits != 0u;
}

// The last filter pass, from the one before's, the pass header's w the target it wrote, its sun given back some of the
// contrast the blur took where the variance is low, into the mask.
[shader("compute")]
[numthreads(8, 8, 1)]
void FilterLastAndBlendShadows(
    uint3 thread : SV_DispatchThreadID,
    uint3 local : SV_GroupThreadID,
    uint3 group : SV_GroupID
)
{
    FrameConstants frame = GetFrame();
    ShadowDenoiseConstants denoise = GetDenoise();
    uint2 size = uint2(denoise.width, denoise.height);
    uint state = LoadTileState(denoise, group.xy);
    float2 sun = Filter(frame, denoise, state, GetPassHeader().w, int2(thread.xy), int2(local.xy), 4);
    if (any(thread.xy >= size))
        return;

    // The sky, which the rays left all lit.
    uint2 picturePixel = GetPicturePixel(frame, thread.xy, size, float2(0.0));
    TextureRef depthTexture = { frame.depth };
    float depth = depthTexture.Load(picturePixel, 0u).x;
    if (depth <= 0.0)
        return;

    float denoised = saturate(pow(abs(sun.x), max(1.2 - sun.y, 1.0)));
    uint4 traced = LoadShadowMask(denoise.mask, thread.xy, size.x);
    Lights lights = GetLights();
    ArrayRef<uint> tiles = { lights.tiles };
    uint tile = GetLightTileOffset(picturePixel / LightTileSize, frame.width);
    uint buckets = (min(lights.count, MaxLights) + 31u) / 32u;

    // A tile without lights has no slot to blend.
    if (!ListsAnyLight(tiles, tile, buckets))
    {
        StoreShadowMask(denoise.mask, thread.xy, size.x, EncodeShadow(traced & TileSlotBits, 0u, denoised));
        return;
    }

    TextureRef velocityTexture = { frame.velocity };
    float4 velocity = velocityTexture.Load(picturePixel, 0u);
    float2 uv = GetTexelCenterUv(thread.xy, size);
    float2 historyUv = uv + velocity.xy;
    bool isReprojected = denoise.hasHistory != 0u && IsOnScreen(historyUv);
    uint2 historyPixel = GetTexelAt(historyUv, size);
    if (isReprojected)
    {
        TextureRef previousMoments = { denoise.previousMoments };
        float tolerance = GetHistoryTolerance(frame, picturePixel, uv, depth);
        isReprojected = IsSameSurface(previousMoments.Load(historyPixel, 0u).y, velocity.z, tolerance);
    }

    // Where last frame saw something else, the rays as they are. Elsewhere each slot blended with last frame's slot of
    // the same light, the more the less it changed and the less the pixel moved. The two frames' tiles list their
    // lights in the same order, so that slot is 1 plus how many lights last frame's tile listed before it. A light that
    // held no slot there starts afresh.
    uint4 words = traced & TileSlotBits;
    if (isReprojected)
    {
        ArrayRef<uint> lastTiles = { denoise.lastLightTiles };
        uint2 historyPicturePixel = GetPicturePixel(frame, historyPixel, size, float2(0.0));
        uint lastTile = GetLightTileOffset(historyPicturePixel / LightTileSize, frame.width);
        uint4 previousWords = LoadShadowMask(denoise.previousMask, historyPixel, size.x);
        float motion = saturate(length(velocity.xy * float2(size)) / 100.0);
        uint bucket = 0u;
        uint bits = tiles[tile];
        uint lastBits = lastTiles[lastTile];
        uint lastSlot = 1u;
        words = uint4(0u);

        // Unrolled, so each slot of this frame is a fixed part of the words.
        [ForceUnroll]
        for (uint slot = 1u; slot < ShadowSlots; slot++)
        {
            // The next light the tile lists, from the next bucket that lists one.
            while (bits == 0u && bucket + 1u < buckets)
            {
                bucket++;
                lastSlot += countbits(lastBits);
                bits = tiles[tile + bucket];
                lastBits = lastTiles[lastTile + bucket];
            }

            if (bits == 0u)
                break;

            uint below = (1u << firstbitlow(bits)) - 1u;
            bool wasListed = (lastBits & (below + 1u)) != 0u;
            uint previousSlot = lastSlot + countbits(lastBits & below);
            bits &= bits - 1u;
            float current = GetShadow(traced, slot);
            if (wasListed && previousSlot < ShadowSlots)
            {
                float previous = GetShadow(previousWords, previousSlot);
                float change = abs(current - previous) / max(current, max(previous, 0.2));
                float blend = lerp(lerp(0.88, 1.0, (1.0 - change) * (1.0 - change)), 0.2, motion);

                // The blended slot as the 8 bits the mask holds, rounded towards the rays' value, so a pixel that
                // stays still keeps moving until it reaches it rather than stopping within a step of it.
                float value = lerp(current, previous, blend);
                current = (current > value ? ceil(value * 255.0) : floor(value * 255.0)) / 255.0;
            }

            words = EncodeShadow(words, slot, current);
        }
    }

    StoreShadowMask(denoise.mask, thread.xy, size.x, EncodeShadow(words, 0u, denoised));
}

// The tile lights' slots of the blended mask blurred with their 3 by 3 neighbours in the pixel's own tile of lights,
// where the slots hold the same lights, each weighted by how near it is to the pixel's plane and how alike its normal
// is, into the filtered mask. The sun's slot, filtered before, and the sky stay as they are, and so do the slots of a
// tile without lights, which are all 0, and of a pixel whose 3 by 3 neighbours all hold the same slots.
[shader("compute")]
[numthreads(8, 8, 1)]
void FilterTileShadows(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    ShadowDenoiseConstants denoise = GetDenoise();
    uint2 size = uint2(denoise.width, denoise.height);
    if (any(thread.xy >= size))
        return;

    uint4 words = LoadShadowMask(denoise.mask, thread.xy, size.x);
    uint2 picturePixel = GetPicturePixel(frame, thread.xy, size, float2(0.0));
    TextureRef depthTexture = { frame.depth };
    float depth = depthTexture.Load(picturePixel, 0u).x;
    if (depth <= 0.0)
    {
        StoreShadowMask(denoise.filteredMask, thread.xy, size.x, words);
        return;
    }

    Lights lights = GetLights();
    ArrayRef<uint> tiles = { lights.tiles };
    uint tile = GetLightTileOffset(picturePixel / LightTileSize, frame.width);
    bool isUniform = !ListsAnyLight(tiles, tile, (min(lights.count, MaxLights) + 31u) / 32u);
    if (!isUniform)
    {
        uint4 slots = words & TileSlotBits;
        isUniform = true;
        for (uint i = 0u; i < 9u; i++)
        {
            int2 offset = int2(int(i % 3u), int(i / 3u)) - 1;
            uint2 neighbour = uint2(clamp(int2(thread.xy) + offset, int2(0), int2(size) - 1));
            isUniform = isUniform && all((LoadShadowMask(denoise.mask, neighbour, size.x) & TileSlotBits) == slots);
        }
    }

    if (isUniform)
    {
        StoreShadowMask(denoise.filteredMask, thread.xy, size.x, words);
        return;
    }

    float3 relative = ReconstructPixel(frame, picturePixel, depth);
    float3 normal = LoadNormal(frame.gbufferNormal, picturePixel);
    float viewDepth = ToViewDepth(depth, frame.near);
    uint2 lightTile = picturePixel / LightTileSize;
    float4 sums[4] = { float4(0.0), float4(0.0), float4(0.0), float4(0.0) };
    float weightSum = 0.0;
    for (int y = -1; y <= 1; y++)
    {
        for (int x = -1; x <= 1; x++)
        {
            uint2 neighbour = uint2(clamp(int2(thread.xy) + int2(x, y), int2(0), int2(size) - 1));
            uint2 neighbourPixel = GetPicturePixel(frame, neighbour, size, float2(0.0));
            float neighbourDepth = depthTexture.Load(neighbourPixel, 0u).x;
            if (neighbourDepth <= 0.0 || any(neighbourPixel / LightTileSize != lightTile))
                continue;

            float3 gap = ReconstructPixel(frame, neighbourPixel, neighbourDepth) - relative;
            float weight = (x == 0 ? 1.0 : 0.5) * (y == 0 ? 1.0 : 0.5);
            weight *= exp(-Pow2(dot(gap, normal) / viewDepth) * FilterPlaneSharpness);
            weight *= pow(saturate(dot(normal, LoadNormal(frame.gbufferNormal, neighbourPixel))), 32.0);
            uint4 neighbourWords = LoadShadowMask(denoise.mask, neighbour, size.x);
            [ForceUnroll]
            for (uint i = 0u; i < 4u; i++)
                sums[i] += weight * UnpackUnorm4x8(neighbourWords[i]);

            weightSum += weight;
        }
    }

    uint4 filtered = uint4(0u);
    [ForceUnroll]
    for (uint i = 0u; i < 4u; i++)
        filtered[i] = PackUnorm4x8(sums[i] / weightSum);

    filtered.x = (filtered.x & ~0xFFu) | (words.x & 0xFFu);
    StoreShadowMask(denoise.filteredMask, thread.xy, size.x, filtered);
}

// This frame's tiles of lights copied, a thread a word, for the next frame to find which slot held each light.
[shader("compute")]
[numthreads(64, 1, 1)]
void KeepLightTiles(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    uint2 tiles = (uint2(frame.width, frame.height) + LightTileSize - 1u) / LightTileSize;
    if (thread.x >= tiles.x * tiles.y * LightBuckets)
        return;

    ArrayRef<uint> frameTiles = { GetLights().tiles };
    ArrayRef<uint> lastTiles = { GetDenoise().lastLightTiles };
    lastTiles[thread.x] = frameTiles[thread.x];
}

// A pixel of the picture's mask and the sun's tint, read from the four traced pixels around it on its surface: the
// sun's slot and tint from each, and a tile light's slot only from those in the pixel's own tile of lights, as a slot
// holds the light its tile lists there, or from the traced pixel that holds it where none counts. The sky is all lit.
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
    bool isTinted = shadows.sunTint != 0u;
    StorageRef tints = { shadows.sunTint, 0u };
    TextureRef tracedTints = { denoise.sunTint };
    TextureRef depthTexture = { frame.depth };
    float depth = depthTexture.Load(thread.xy, 0u).x;
    if (depth == 0.0)
    {
        StoreShadowMask(shadows.mask, thread.xy, size.x, uint4(0xFFFFFFFFu));
        if (isTinted)
            tints.Store(thread.xy, float4(1.0));

        return;
    }

    float3 relative = ReconstructPixel(frame, thread.xy, depth);
    float3 normal = LoadNormal(frame.gbufferNormal, thread.xy);
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
        words[i] = LoadShadowMask(denoise.filteredMask, texel, tracedSize.x);
        if (isTinted)
            tint += tracedTints.Load(texel, 0u).rgb * taps.weights[i];

        bool isSameTile = all(GetPicturePixel(frame, texel, tracedSize, float2(0.0)) / LightTileSize == tile);
        tileWeights[i] = isSameTile ? taps.weights[i] : 0.0;
    }

    float sunSum = dot(taps.weights, float4(1.0));
    float tileSum = dot(tileWeights, float4(1.0));
    uint2 nearest = GetTracedTexel(frame, thread.xy, tracedSize);
    uint4 nearestWords = LoadShadowMask(denoise.filteredMask, nearest, tracedSize.x);
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
    if (isTinted)
        tints.Store(thread.xy, float4(sunSum > 0.0 ? tint / sunSum : tracedTints.Load(nearest, 0u).rgb, 1.0));
}
