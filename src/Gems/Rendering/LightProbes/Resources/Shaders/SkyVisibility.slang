// How much of the sky each place sees, from the sky's direction maps: a depth map of the still world for each of a
// fixed set of directions on the upper hemisphere, as a large spherical array of shadow-mapped lights (Pharr and Green,
// "Ambient Occlusion", GPU Gems, 2004, ch. 17). A point sees the sky along a direction where nothing in that
// direction's map lies between it and the sky. The far maps test a volume of probes, each the first two bands of
// spherical harmonics of what it sees; the near maps test each pixel near the camera, each over its cone of the sky.
// Past the probes and the far maps, the sky is open. The pass header's x is the frame's buffer and its y the buffer of
// SkyVisibilityConstants.
module "SkyVisibility";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Scene.ShadowViews;
import Light.Ambient;
import Core.Random;

// What the passes read beyond the frame, C#'s SkyVisibilityConstants, 112 bytes.
struct SkyVisibilityConstants
{
    // The views of the near and far maps, their atlases, and the sampler that clamps.
    uint nearMaps;
    uint nearCount;
    uint farMaps;
    uint farCount;
    uint nearAtlas;
    uint farAtlas;
    uint sampler;
    // The probes' volume, the traced visibility at half size and its blur.
    uint probes;
    uint traced;
    uint blurred;
    uint halfWidth;
    uint halfHeight;
    // The world position the maps' views are relative to, and the metres a point moves along its normal.
    float3 anchor;
    float normalOffset;
    // The world position of the probes' first corner, and the metres between probes.
    float3 probeOrigin;
    float probeCell;
    uint3 probeCounts;
    // The metres from the camera inside which the near maps alone decide, and past which the probes alone do.
    float nearDistance;
    float farDistance;
    // Metres of depth that still count as the point itself, against the depth's error.
    float depthBias;
    // The first z of the slab of probes that this dispatch tests.
    uint probeFirstZ;
    float padding;
};

// The widest angle from the mirror direction, as a cosine, of the directions that the specular visibility counts.
static const float SpecularCone = 0.8660254;

// The golden angle in radians, which spreads a spiral's samples evenly.
static const float GoldenAngle = 2.3999632;

SkyVisibilityConstants LoadVisibilityConstants() { return LoadConstants<SkyVisibilityConstants>(GetPassHeader().y); }

// How much of the sky a point relative to the maps' anchor sees along one map's direction, from 0 to 1: a bilinear
// test of the 4 texels around it, so the answer steps smoothly between texels. -1 where the map is not drawn or does
// not hold the point.
float TestMap(GpuShadowView view, TextureRef atlas, SamplerRef sampler, float3 point, float bias)
{
    if (view.rect.z == 0u)
        return -1.0;

    float4 clip = mul(float4(point, 1.0), view.viewProjection);
    float2 uv = float2(clip.x * 0.5 + 0.5, 0.5 - clip.y * 0.5);
    if (any(uv <= 0.0) || any(uv >= 1.0))
        return -1.0;

    // A point above the near plane sees the sky.
    float depth = clip.z;
    if (depth >= 1.0)
        return 1.0;

    float2 size = float2(atlas.GetSize(0u));
    float2 texel = float2(view.rect.xy) + uv * float2(view.rect.zw);
    float4 depths = atlas.GatherRed(sampler, texel / size);
    float reference = depth + bias / view.depthRange;
    // Gather's order: lower left, lower right, upper right, upper left. A texel nearer the sky than the point hides it.
    float4 seen = float4(depths <= reference);
    float2 f = frac(texel - 0.5);
    return lerp(lerp(seen.w, seen.z, f.x), lerp(seen.x, seen.y, f.x), f.y);
}

// The texels of the near maps that a soft test reads at most from the point, and the metres above it that its search
// for blockers reaches.
static const float ConeMaxTexels = 16.0;
static const float ConeSearchMeters = 8.0;

// The samples of a soft test's filter.
static const uint ConeTaps = 8u;

// How much of the sky a point sees along one map's direction, from 0 to 1, over the cone of the sky that the map stands
// for: the map is filtered as a light as wide as that cone, as percentage-closer soft shadows filter a light (Fernando,
// "Percentage-Closer Soft Shadows", 2005). The blockers found about the point give their mean distance, and the
// filter's radius is that distance times the tangent of the cone's half angle. So the sky through an opening falls on a
// surface as a soft patch, as wide as the cone makes it, and not as the sharp shape of the opening that each direction
// alone casts. The filter turns by rotation, which the blur and the temporal anti-aliasing average. -1 where the map is
// not drawn or does not hold the point.
float TestMapOverCone(
    GpuShadowView view,
    TextureRef atlas,
    SamplerRef sampler,
    float3 point,
    float bias,
    float coneTangent,
    float rotation
)
{
    if (view.rect.z == 0u)
        return -1.0;

    float4 clip = mul(float4(point, 1.0), view.viewProjection);
    float2 uv = float2(clip.x * 0.5 + 0.5, 0.5 - clip.y * 0.5);
    if (any(uv <= 0.0) || any(uv >= 1.0))
        return -1.0;

    // A point above the near plane sees the sky.
    float depth = clip.z;
    if (depth >= 1.0)
        return 1.0;

    // The reads keep inside the map's own rectangle of the atlas.
    float2 size = float2(atlas.GetSize(0u));
    float2 lowest = float2(view.rect.xy) + 1.0;
    float2 highest = float2(view.rect.xy + view.rect.zw) - 1.0;
    float2 texel = float2(view.rect.xy) + uv * float2(view.rect.zw);
    float reference = depth + bias / view.depthRange;
    float2 turn = float2(cos(rotation), sin(rotation));

    // The blockers: the point's texels and four more on a ring as wide as the widest filter the search reaches.
    float ring = 0.7 * min(ConeSearchMeters * coneTangent / view.texelSize, ConeMaxTexels);
    float blockers = 0.0;
    float blockerDepth = 0.0;
    for (uint i = 0u; i < 5u; i++)
    {
        float2 offset = float2(0.0);
        if (i > 0u)
        {
            float2 side = i == 1u ? float2(1.0, 0.0) : i == 2u ? float2(0.0, 1.0) : i == 3u ? float2(-1.0, 0.0)
                : float2(0.0, -1.0);
            offset = float2(side.x * turn.x - side.y * turn.y, side.x * turn.y + side.y * turn.x) * ring;
        }

        float4 depths = atlas.GatherRed(sampler, clamp(texel + offset, lowest, highest) / size);
        // A texel nearer the sky than the point blocks it.
        float4 isBlocking = float4(depths > reference);
        blockers += dot(isBlocking, float4(1.0));
        blockerDepth += dot(isBlocking, depths - depth);
    }

    if (blockers == 0.0)
        return 1.0;

    // The filter over a disc as wide as the cone is at the blockers' mean distance, its samples on a Vogel spiral.
    float distance = blockerDepth / blockers * view.depthRange;
    float radius = clamp(distance * coneTangent / view.texelSize, 0.5, ConeMaxTexels);
    float seen = 0.0;
    for (uint tap = 0u; tap < ConeTaps; tap++)
    {
        float along = sqrt((float(tap) + 0.5) / float(ConeTaps)) * radius;
        float angle = float(tap) * GoldenAngle + rotation;
        float2 offset = float2(cos(angle), sin(angle)) * along;
        float4 depths = atlas.GatherRed(sampler, clamp(texel + offset, lowest, highest) / size);
        seen += dot(float4(depths <= reference), float4(0.25));
    }

    return seen / float(ConeTaps);
}

// The cosine-weighted share of the sky a point sees from the near maps for a normal, and the share it sees near the
// mirror direction; the mean of every drawn direction where none faces the normal or lies near the mirror. Each map
// is tested over its share of the sky, a cone whose cap is one map's share of the hemisphere.
float2 TestNearMaps(SkyVisibilityConstants constants, float3 point, float3 normal, float3 mirror, float rotation)
{
    TextureRef atlas = { constants.nearAtlas };
    SamplerRef sampler = { constants.sampler };
    float coneTangent = tan(acos(1.0 - 1.0 / max(float(constants.nearCount), 1.0)));
    float diffuse = 0.0;
    float diffuseWeight = 0.0;
    float specular = 0.0;
    float specularCount = 0.0;
    float sum = 0.0;
    float count = 0.0;
    for (uint i = 0u; i < constants.nearCount; i++)
    {
        GpuShadowView view = LoadShadowView(constants.nearMaps, i);
        float seen = TestMapOverCone(view, atlas, sampler, point, constants.depthBias, coneTangent, rotation);
        if (seen < 0.0)
            continue;

        float facing = max(dot(normal, view.towards), 0.0);
        diffuse += seen * facing;
        diffuseWeight += facing;
        sum += seen;
        count += 1.0;
        if (dot(mirror, view.towards) >= SpecularCone)
        {
            specular += seen;
            specularCount += 1.0;
        }
    }

    if (count == 0.0)
        return float2(1.0);

    float mean = sum / count;
    float diffuseShare = diffuseWeight > 1e-3 ? diffuse / diffuseWeight : mean;
    float specularShare = specularCount > 0.0 ? specular / specularCount : diffuseShare;
    return float2(diffuseShare, specularShare);
}

// The bands of the cosine lobe over pi, for a visibility in the first two bands of spherical harmonics.
static const float BandZero = 1.0;
static const float BandOne = 2.0 / 3.0;

// The first two bands of the spherical harmonics of an open upper hemisphere: what a probe in the open holds.
static const float4 OpenSky = float4(0.282095 * TwoPi, 0.488603 * Pi, 0.0, 0.0);

float4 GetBandsAt(float3 d) { return float4(0.282095, 0.488603 * d.y, 0.488603 * d.z, 0.488603 * d.x); }

// The share of the sky a probe's harmonics see for a normal, cosine-weighted, and along a mirror direction, each over
// what the open sky gives there; where the open sky gives little, such as a normal facing down, the mean over the whole
// hemisphere.
float2 EvaluateProbe(float4 harmonics, float3 normal, float3 mirror)
{
    float4 cosine = GetBandsAt(normal) * float4(BandZero, BandOne, BandOne, BandOne);
    float seen = max(dot(harmonics, cosine), 0.0);
    float open = max(dot(OpenSky, cosine), 0.0);
    float mean = saturate(harmonics.x / OpenSky.x);
    float diffuse = saturate((seen + 0.05 * mean) / (open + 0.05));
    float4 along = GetBandsAt(mirror);
    float mirrorSeen = max(dot(harmonics, along), 0.0);
    float mirrorOpen = dot(OpenSky, along);
    float specular = mirror.y > 0.0 ? saturate((mirrorSeen + 0.05 * mean) / (max(mirrorOpen, 0.0) + 0.05)) : diffuse;
    return float2(diffuse, specular);
}

// The probes' harmonics at a world position, read between probes; an open sky outside their volume.
float4 SampleProbes(SkyVisibilityConstants constants, float3 world)
{
    float3 cell = (world - constants.probeOrigin) / constants.probeCell;
    float3 counts = float3(constants.probeCounts);
    if (any(cell < 0.0) || any(cell > counts - 1.0))
        return OpenSky;

    VolumeRef probes = { constants.probes };
    SamplerRef sampler = { constants.sampler };
    return probes.SampleLevel(sampler, (cell + 0.5) / counts, 0.0);
}

// Each probe of the volume: what it sees along each far direction, projected onto the first two bands. The directions
// are cosine-weighted, so each counts over its density, pi over the cosine of its angle from the zenith.
[shader("compute")]
[numthreads(4, 4, 4)]
void TestSkyProbes(uint3 thread : SV_DispatchThreadID)
{
    SkyVisibilityConstants constants = LoadVisibilityConstants();
    uint3 probe = thread + uint3(0u, 0u, constants.probeFirstZ);
    if (any(probe >= constants.probeCounts))
        return;

    float3 world = constants.probeOrigin + float3(probe) * constants.probeCell;
    float3 point = world - constants.anchor;
    TextureRef atlas = { constants.farAtlas };
    SamplerRef sampler = { constants.sampler };
    float4 harmonics = float4(0.0);
    float drawn = 0.0;
    for (uint i = 0u; i < constants.farCount; i++)
    {
        GpuShadowView view = LoadShadowView(constants.farMaps, i);
        float seen = TestMap(view, atlas, sampler, point, constants.depthBias);
        if (seen < 0.0)
            continue;

        float3 d = view.towards;
        harmonics += seen * GetBandsAt(d) * Pi / max(d.y, 0.05);
        drawn += 1.0;
    }

    StorageVolumeRef probes = { constants.probes, 0u };
    probes.Store(probe, drawn > 0.0 ? harmonics / drawn : OpenSky);
}

// The share of the sky each pixel sees at half size, diffuse in red and specular in green: from the near maps inside
// the near distance, from the probes past the far one, and blended between.
[shader("compute")]
[numthreads(8, 8, 1)]
void TraceSkyVisibility(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    SkyVisibilityConstants constants = LoadVisibilityConstants();
    if (any(thread.xy >= uint2(constants.halfWidth, constants.halfHeight)))
        return;

    StorageRef traced = { constants.traced, 0u };
    uint2 pixel = min(thread.xy * 2u, uint2(frame.width, frame.height) - 1u);
    TextureRef depthTexture = { frame.depth };
    float depth = depthTexture.Load(pixel, 0u).x;
    if (depth == 0.0)
    {
        traced.Store(thread.xy, float4(1.0, 1.0, 0.0, 0.0));
        return;
    }

    float3 relative = ReconstructPixel(frame, pixel, depth);
    float3 normal = LoadNormal(frame.gbufferNormal, pixel);
    float3 view = NormalizeSafe(-relative, float3(0.0, 0.0, -1.0));
    float3 mirror = reflect(-view, normal);
    float distance = length(relative);
    float3 world = relative + frame.cameraPosition.xyz;

    float2 far = float2(1.0);
    if (distance > constants.nearDistance)
    {
        // Half a cell along the normal and a quarter towards the camera, off the surface the probes see from behind.
        float3 at = world + normal * (0.5 * constants.probeCell) + view * (0.25 * constants.probeCell);
        far = EvaluateProbe(SampleProbes(constants, at), normal, mirror);
    }

    float2 near = float2(1.0);
    if (distance < constants.farDistance)
    {
        // A turn for each pixel that changes each frame, so the blur and the temporal anti-aliasing average the
        // filter's pattern away.
        float rotation = GetInterleavedGradientNoise(pixel, frame.frameIndex) * TwoPi;
        near = TestNearMaps(constants, world + normal * constants.normalOffset - constants.anchor, normal, mirror,
            rotation);
    }

    float blend = saturate((distance - constants.nearDistance) / max(constants.farDistance - constants.nearDistance,
        Epsilon));
    traced.Store(thread.xy, float4(lerp(near, far, blend), 0.0, 0.0));
}

// A 5 by 5 blur of the traced visibility that keeps to surfaces: each neighbour weighed by how near its depth and its
// normal are to the pixel's.
[shader("compute")]
[numthreads(8, 8, 1)]
void BlurSkyVisibility(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    SkyVisibilityConstants constants = LoadVisibilityConstants();
    int2 size = int2(constants.halfWidth, constants.halfHeight);
    if (any(int2(thread.xy) >= size))
        return;

    TextureRef traced = { constants.traced };
    TextureRef depthTexture = { frame.depth };
    int2 full = int2(frame.width, frame.height) - 1;
    uint2 pixel = uint2(min(int2(thread.xy) * 2, full));
    float depth = depthTexture.Load(pixel, 0u).x;
    StorageRef blurred = { constants.blurred, 0u };
    if (depth == 0.0)
    {
        blurred.Store(thread.xy, float4(1.0, 1.0, 0.0, 0.0));
        return;
    }

    float viewDepth = frame.near / depth;
    float3 normal = LoadNormal(frame.gbufferNormal, pixel);
    float2 sum = float2(0.0);
    float weight = 0.0;
    for (int y = -2; y <= 2; y++)
    {
        for (int x = -2; x <= 2; x++)
        {
            int2 at = clamp(int2(thread.xy) + int2(x, y), int2(0), size - 1);
            uint2 atPixel = uint2(min(at * 2, full));
            float atDepth = depthTexture.Load(atPixel, 0u).x;
            if (atDepth == 0.0)
                continue;

            float depthWeight = exp(-abs(frame.near / atDepth - viewDepth) / max(0.05 * viewDepth, 1e-3));
            float normalWeight = pow(saturate(dot(LoadNormal(frame.gbufferNormal, atPixel), normal)), 8.0);
            float w = depthWeight * normalWeight + 1e-4;
            sum += traced.Load(uint2(at), 0u).xy * w;
            weight += w;
        }
    }

    blurred.Store(thread.xy, float4(sum / max(weight, Epsilon), 0.0, 0.0));
}
