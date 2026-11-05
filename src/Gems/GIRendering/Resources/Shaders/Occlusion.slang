// The short-range occlusion: how much of the hemisphere above each pixel's surface the surfaces drawn around it
// hide, from the depth and the normals alone, over a few pixels, then blurred along its own depth. It is what the
// screen probes cannot keep apart at a contact: a crease, the foot of a wall, the ground under a thing. After
// Jimenez et al., "Practical Realtime Strategies for Accurate Indirect Occlusion", 2016, with horizons searched in
// the world. The pass header's x is the frame's constants and z the lighting constants.
module "Occlusion";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Core.Pack;
import Core.Random;
import Scene.Lighting;
import Scene.Probes;

static const uint Directions = 8u;
static const uint Steps = 4u;
static const float RadiusPixels = 16.0;
static const float RadiusMetres = 0.5;
static const int BlurReach = 2;

// The camera-relative position and the normal the pixel holds; false where nothing is drawn.
bool LoadPixel(FrameConstants frame, int2 pixel, uint2 size, out float3 relative, out float3 normal, out float viewDepth)
{
    relative = float3(0.0);
    normal = float3(0.0, 1.0, 0.0);
    viewDepth = 0.0;
    TextureRef depthTexture = { frame.depth };
    float depth = depthTexture.Load(uint2(pixel), 0u).x;
    if (depth <= 0.0)
        return false;

    TextureRef normalTexture = { frame.gbufferNormal };
    relative = ReconstructRelative(frame, GetTexelCenterUv(uint2(pixel), size), depth);
    normal = DecodeOctahedral(normalTexture.Load(uint2(pixel), 0u).xy * 2.0 - 1.0);
    viewDepth = ToViewDepth(depth, frame.near);
    return true;
}

// Each pixel: along each of a few directions on the screen, turned differently from pixel to pixel, the highest
// surface seen within the radius, in the frame of the pixel's normal; what those horizons leave of the hemisphere.
[shader("compute")]
[numthreads(8, 8, 1)]
void Occlusion(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    LightingConstants lighting = GetLighting();
    GiConstants gi = LoadConstants<GiConstants>(lighting.gi);
    uint2 size = uint2(frame.width, frame.height);
    if (any(thread.xy >= size))
        return;

    StorageRef target = { gi.occlusionRaw, 0u };
    float3 relative;
    float3 normal;
    float viewDepth;
    if (!LoadPixel(frame, int2(thread.xy), size, relative, normal, viewDepth))
    {
        target.Store(thread.xy, float4(1.0));
        return;
    }

    // The radius on screen: what the metres cover at this depth, kept between a few pixels and RadiusPixels, so the
    // search is as cheap far away as near.
    float radius = clamp(RadiusMetres * frame.pixelsPerRadian / viewDepth, 3.0, RadiusPixels);
    float turn = ToUnitFloat(GetHash(thread.x + thread.y * 7919u)) * TwoPi / float(Directions);
    float visible = 0.0;
    for (uint d = 0u; d < Directions; d++)
    {
        float angle = turn + float(d) * TwoPi / float(Directions);
        float2 direction = float2(cos(angle), sin(angle));
        float horizon = 0.0;
        for (uint s = 1u; s <= Steps; s++)
        {
            float2 offset = direction * (radius * float(s) / float(Steps));
            int2 sample = int2(round(float2(thread.xy) + offset));
            if (any(sample < 0) || any(sample >= int2(size)))
                break;

            float3 other;
            float3 otherNormal;
            float otherDepth;
            if (!LoadPixel(frame, sample, size, other, otherNormal, otherDepth))
                break;

            float3 toOther = other - relative;
            float distance = length(toOther);
            if (distance < 1e-4)
                continue;

            // Only what is within twice the radius in metres counts, fading out towards it.
            float falloff = saturate(1.0 - distance / (RadiusMetres * 2.0));
            float elevation = dot(toOther / distance, normal);
            horizon = max(horizon, elevation * falloff);
        }

        visible += 1.0 - horizon;
    }

    target.Store(thread.xy, float4(visible / float(Directions)));
}

// Each pixel's occlusion blended with its neighbours' on about the same depth, which takes the turn between pixels
// out.
[shader("compute")]
[numthreads(8, 8, 1)]
void OcclusionBlur(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    LightingConstants lighting = GetLighting();
    GiConstants gi = LoadConstants<GiConstants>(lighting.gi);
    uint2 size = uint2(frame.width, frame.height);
    if (any(thread.xy >= size))
        return;

    TextureRef raw = { gi.occlusionRaw };
    TextureRef depthTexture = { frame.depth };
    StorageRef target = { gi.occlusion, 0u };
    float depth = depthTexture.Load(thread.xy, 0u).x;
    if (depth <= 0.0)
    {
        target.Store(thread.xy, float4(1.0));
        return;
    }

    float viewDepth = ToViewDepth(depth, frame.near);
    float sum = 0.0;
    float weightSum = 0.0;
    for (int y = -BlurReach; y <= BlurReach; y++)
    {
        for (int x = -BlurReach; x <= BlurReach; x++)
        {
            int2 at = clamp(int2(thread.xy) + int2(x, y), int2(0), int2(size) - 1);
            float otherDepth = depthTexture.Load(uint2(at), 0u).x;
            if (otherDepth <= 0.0)
                continue;

            float closeness = saturate(1.0 - abs(ToViewDepth(otherDepth, frame.near) - viewDepth) / (0.05 * viewDepth));
            sum += raw.Load(uint2(at), 0u).x * closeness;
            weightSum += closeness;
        }
    }

    target.Store(thread.xy, float4(weightSum > 0.0 ? sum / weightSum : 1.0));
}
