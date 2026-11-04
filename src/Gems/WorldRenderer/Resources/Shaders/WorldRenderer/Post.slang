// The picture after the lighting, to the output: the panes composed over it; that resolved over time into the
// output's size, each frame's jittered picture reprojected into the last's; the exposure metered from a histogram
// of what the frame holds; bloom from what is brightest; and the final pass, which scales by the exposure, runs the
// camera's effects, tonemaps and encodes for the screen, or shows a debug view. The pass header's x is the frame's
// constants, y the shadow constants, z the lighting constants and w the field constants.
module "WorldRenderer/Post";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Core.Color;
import Core.Pack;
import Scene.Lighting;
import Scene.Field;
import Scene.Translucency;
import Scene.Post;
import Post.IPixelEffect;
import Trace.DistanceField;

static const float HistoryWeight = 0.9;
static const float ExposureSpeed = 0.05;

// --- Compose ---

// The panes composed over the picture, in the picture's size: the picture behind them, bent, through their colour,
// plus their own light; the picture as it is where there are none.
[shader("compute")]
[numthreads(8, 8, 1)]
void PostCompose(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    LightingConstants lighting = GetLighting();
    uint2 size = uint2(frame.width, frame.height);
    if (any(thread.xy >= size))
        return;

    TextureRef hdr = { lighting.hdr };
    TranslucencyConstants translucency = GetTranslucency();
    float3 color = hdr.Load(thread.xy, 0u).rgb;
    if (translucency.hasTranslucent != 0u)
    {
        TextureRef optical = { translucency.opticalDepth };
        float4 absorbance = optical.Load(thread.xy, 0u);
        if (absorbance.w > 0.0)
        {
            TextureRef accumulation = { translucency.accumulation };
            TextureRef offsets = { translucency.offsets };
            float4 added = accumulation.Load(thread.xy, 0u);
            float2 offset = offsets.Load(thread.xy, 0u).xy / max(added.w, 1e-4);
            uint2 behind = uint2(clamp(int2(thread.xy) + int2(offset * float2(size)), int2(0), int2(size) - 1));
            color = hdr.Load(behind, 0u).rgb * exp(-absorbance.rgb) + added.rgb;
        }
    }

    PostConstants post = GetPost();
    StorageRef picture = { post.picture, 0u };
    picture.Store(thread.xy, float4(color, 1.0));
}

// --- Resolve ---

// Each output pixel: the picture at its jittered place, blended with what last frame resolved at the same surface,
// held within what its neighbours hold now; the resolved view depth kept beside it for next frame's test.
[shader("compute")]
[numthreads(8, 8, 1)]
void PostResolve(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    PostConstants post = GetPost();
    uint2 size = uint2(frame.outputWidth, frame.outputHeight);
    if (any(thread.xy >= size))
        return;

    uint2 inner = uint2(frame.width, frame.height);
    float2 uv = GetTexelCenterUv(thread.xy, size);
    float2 jittered = uv + frame.jitter.xy / float2(inner);
    TextureRef picture = { post.picture };
    SamplerRef sampler = { post.sampler };
    float3 current = picture.SampleLevel(sampler, jittered, 0.0).rgb;

    // The neighbourhood: the least and most of the picture's texels around the place.
    int2 centre = int2(clamp(jittered * float2(inner), float2(0.0), float2(inner) - 1.0));
    float3 low = current;
    float3 high = current;
    for (int y = -1; y <= 1; y++)
    {
        for (int x = -1; x <= 1; x++)
        {
            uint2 texel = uint2(clamp(centre + int2(x, y), int2(0), int2(inner) - 1));
            float3 near = picture.Load(texel, 0u).rgb;
            low = min(low, near);
            high = max(high, near);
        }
    }

    TextureRef depthTexture = { frame.depth };
    float depth = depthTexture.Load(uint2(centre), 0u).x;
    float3 relative = ReconstructRelative(frame, jittered, depth > 0.0 ? depth : 1e-6);
    float viewDepth = mul(float4(relative, 1.0), frame.view).z;
    float3 result = current;
    float4 previousClip = mul(float4(relative + frame.cameraPosition.xyz - frame.previousCameraPosition.xyz, 1.0), frame.previousViewProjection);
    if (previousClip.w > frame.near)
    {
        float2 previousUv = float2(previousClip.x / previousClip.w * 0.5 + 0.5, 0.5 - previousClip.y / previousClip.w * 0.5);
        if (all(previousUv >= 0.0) && all(previousUv < 1.0))
        {
            TextureRef history = { post.historyPrevious };
            float4 held = history.SampleLevel(sampler, previousUv, 0.0);
            bool isSky = depth <= 0.0;
            bool isSame = isSky ? held.w <= 0.0 : (held.w > 0.0 && abs(held.w - previousClip.w) < 0.05 * previousClip.w);
            if (isSame)
                result = lerp(current, clamp(held.rgb, low, high), HistoryWeight);
        }
    }

    StorageRef output = { post.history, 0u };
    output.Store(thread.xy, float4(result, depth > 0.0 ? viewDepth : 0.0));
}

// --- Exposure ---

groupshared uint localBins[PostHistogramBins];

// The histogram of the resolved picture's luminance, in the log of it, over the bins' range.
[shader("compute")]
[numthreads(16, 16, 1)]
void PostHistogram(uint3 thread : SV_DispatchThreadID, uint3 local : SV_GroupThreadID)
{
    FrameConstants frame = GetFrame();
    PostConstants post = GetPost();
    uint localIndex = local.y * 16u + local.x;
    localBins[localIndex] = 0u;
    GroupMemoryBarrierWithGroupSync();

    uint2 size = uint2(frame.outputWidth, frame.outputHeight);
    if (all(thread.xy < size))
    {
        TextureRef history = { post.history };
        float luminance = GetLuminance(history.Load(thread.xy, 0u).rgb);
        uint bin = GetHistogramBin(luminance);
        InterlockedAdd(localBins[bin], 1u);
    }

    GroupMemoryBarrierWithGroupSync();
    BufferRef histogram = { post.histogram };
    if (localBins[localIndex] != 0u)
        histogram.InterlockedAdd(localIndex * 4u, localBins[localIndex]);
}

groupshared uint countBins[PostHistogramBins];

// The exposure from the histogram: the mean luminance of the middle of the picture, the darkest and brightest
// shares left out, brought to a middle grey, and eased towards from last frame's.
[shader("compute")]
[numthreads(256, 1, 1)]
void PostExposure(uint3 local : SV_GroupThreadID)
{
    PostConstants post = GetPost();
    BufferRef histogram = { post.histogram };
    uint bin = local.x;
    uint count = histogram.Load<uint>(bin * 4u);
    countBins[bin] = count;
    histogram.Store<uint>(bin * 4u, 0u);
    GroupMemoryBarrierWithGroupSync();
    if (bin != 0u)
        return;

    uint total = 0u;
    for (uint i = 0u; i < PostHistogramBins; i++)
    {
        total += countBins[i];
    }

    // The bins between the percentiles, each weighed by what of it lies within them.
    float low = float(total) * 0.1;
    float high = float(total) * 0.9;
    float seen = 0.0;
    float weightedSum = 0.0;
    float weightSum = 0.0;
    for (uint i = 0u; i < PostHistogramBins; i++)
    {
        float binCount = float(countBins[i]);
        float from = max(seen, low);
        float to = min(seen + binCount, high);
        float within = max(to - from, 0.0);
        weightedSum += GetHistogramLuminance(i) * within;
        weightSum += within;
        seen += binCount;
    }

    BufferRef state = { post.exposureState };
    float previous = state.Load<float>(0u);
    float mean = weightSum > 0.0 ? exp2(weightedSum / weightSum) : 1.0;
    float target = clamp(0.18 * exp2(post.exposureCompensation) / max(mean, 1e-6), post.exposureMin, post.exposureMax);
    float exposure = previous > 0.0 ? lerp(previous, target, ExposureSpeed) : target;
    state.Store<float>(0u, exposure);
}

// --- Bloom ---

// A level of the bloom from the one above it, the first from the resolved picture by what in it is brighter than
// the exposure leaves white: the four texels under each, blended. The pass header's y is the level written.
[shader("compute")]
[numthreads(8, 8, 1)]
void PostBloomDown(uint3 thread : SV_DispatchThreadID)
{
    PostConstants post = GetPost();
    uint level = GetPassHeader().y;
    StorageRef target = { post.bloom, level };
    uint2 size = target.GetSize();
    if (any(thread.xy >= size))
        return;

    SamplerRef sampler = { post.sampler };
    float2 uv = GetTexelCenterUv(thread.xy, size);
    float3 color;
    if (level == 0u)
    {
        TextureRef history = { post.history };
        BufferRef state = { post.exposureState };
        float exposure = state.Load<float>(0u);
        color = history.SampleLevel(sampler, uv, 0.0).rgb;
        float brightness = GetLuminance(color) * exposure;
        color *= smoothstep(1.0, 3.0, brightness);
    }
    else
    {
        TextureRef source = { post.bloom };
        color = source.SampleLevel(sampler, uv, float(level - 1u)).rgb;
    }

    target.Store(thread.xy, float4(color, 1.0));
}

// A level of the bloom with the one below it added, blended up, from the coarsest to the finest. The pass header's
// y is the level written.
[shader("compute")]
[numthreads(8, 8, 1)]
void PostBloomUp(uint3 thread : SV_DispatchThreadID)
{
    PostConstants post = GetPost();
    uint level = GetPassHeader().y;
    StorageRef target = { post.bloom, level };
    uint2 size = target.GetSize();
    if (any(thread.xy >= size))
        return;

    SamplerRef sampler = { post.sampler };
    TextureRef source = { post.bloom };
    float2 uv = GetTexelCenterUv(thread.xy, size);
    float3 color = target.Load(thread.xy).rgb + source.SampleLevel(sampler, uv, float(level + 1u)).rgb;
    target.Store(thread.xy, float4(color, 1.0));
}

// --- Final ---

// The AgX tonemap: the picture's linear light through AgX's base curve, which keeps the hue of what is bright
// (Sobotka, AgX, 2022), back to linear light for the encoding. The matrices are written as columns, as the
// reference lists them, so a row vector goes through them on the left.
float3 Agx(float3 linear)
{
    float3x3 toAgx = float3x3(
        0.842479062253094, 0.0423282422610123, 0.0423756549057051,
        0.0784335999999992, 0.878468636469772, 0.0784336,
        0.0792237451477643, 0.0791661274605434, 0.879142973793104
    );
    float3x3 fromAgx = float3x3(
        1.19687900512017, -0.0528968517574562, -0.0529716355144438,
        -0.0980208811401368, 1.15190312990417, -0.0980434501171241,
        -0.0990297440797205, -0.0989611768448433, 1.15107367264116
    );
    float minEv = -12.47393;
    float maxEv = 4.026069;
    float3 value = mul(max(linear, 1e-10), toAgx);
    value = clamp(log2(value), minEv, maxEv);
    value = (value - minEv) / (maxEv - minEv);
    float3 x2 = value * value;
    float3 x4 = x2 * x2;
    value = 15.5 * x4 * x2 - 40.14 * x4 * value + 31.96 * x4 - 6.868 * x2 * value + 0.4298 * x2 + 0.1191 * value - 0.00232;
    value = mul(saturate(value), fromAgx);
    return pow(saturate(value), 2.2);
}

// Each output pixel: the resolved picture with the bloom, scaled by the exposure, through the camera's effects,
// tonemapped and encoded; or a debug view in its place.
[shader("compute")]
[numthreads(8, 8, 1)]
void PostFinal<E0 : IPixelEffect, E1 : IPixelEffect, E2 : IPixelEffect, E3 : IPixelEffect>(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    LightingConstants lighting = GetLighting();
    PostConstants post = GetPost();
    uint2 size = uint2(frame.outputWidth, frame.outputHeight);
    if (any(thread.xy >= size))
        return;

    TextureRef history = { post.history };
    TextureRef bloom = { post.bloom };
    SamplerRef sampler = { post.sampler };
    BufferRef state = { post.exposureState };
    float exposure = state.Load<float>(0u);
    float2 uv = GetTexelCenterUv(thread.xy, size);
    float3 color = history.Load(thread.xy, 0u).rgb;
    // The shadow views are shown as they are: no bloom, exposure, effect or tonemap.
    bool isShadowView = lighting.debugView == 6u || lighting.debugView == 7u;
    if (!isShadowView)
    {
        color += bloom.SampleLevel(sampler, uv, 0.0).rgb * post.bloomStrength;
        color = max(color * exposure, 0.0);

        PixelEffectInputs inputs;
        inputs.uv = uv;
        inputs.pixel = thread.xy;
        inputs.size = size;
        inputs.exposure = exposure;
        inputs.time = post.time;
        color = E0.Apply(color, inputs);
        color = E1.Apply(color, inputs);
        color = E2.Apply(color, inputs);
        color = E3.Apply(color, inputs);
        color = Agx(color);
    }

    if (lighting.debugView == 1u || lighting.debugView == 2u)
    {
        // The field at the surface, or two voxels above it along the normal: green where it reads the distance to
        // the surface, red where it reads farther out, blue where it reads farther in, the more the farther, in
        // voxels.
        uint2 inner = uint2(frame.width, frame.height);
        uint2 pixel = min(uint2(uv * float2(inner)), inner - 1u);
        UintTextureRef visibility = { frame.visibility };
        if (visibility.Load(pixel, 0u) != 0u)
        {
            TextureRef depthTexture = { frame.depth };
            TextureRef normalTexture = { frame.gbufferNormal };
            float depth = depthTexture.Load(pixel, 0u).x;
            float3 world = ReconstructWorld(frame, GetTexelCenterUv(pixel, inner), depth);
            float3 normal = DecodeOctahedral(normalTexture.Load(pixel, 0u).xy * 2.0 - 1.0);
            FieldConstants field = GetFieldConstants();
            uint level;
            SampleField(field, world, level);
            float above = lighting.debugView == 2u ? 2.0 * field.voxelSizes[level] : 0.0;
            float sampled = (SampleField(field, world + normal * above, level) - above) / field.voxelSizes[level];
            float amount = saturate(abs(sampled) / 4.0);
            color = sampled >= 0.0 ? lerp(float3(0.0, 1.0, 0.0), float3(1.0, 0.0, 0.0), amount) : lerp(float3(0.0, 1.0, 0.0), float3(0.0, 0.0, 1.0), amount);
        }
    }

    StorageRef output = { frame.output, 0u };
    output.Store(thread.xy, float4(ToSrgb(color), 1.0));
}
