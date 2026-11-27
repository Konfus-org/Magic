// How much of the sky each place sees, from the sky's direction maps: a depth map of the still world for each of a
// fixed set of directions on the upper hemisphere, as a large spherical array of shadow-mapped lights (Pharr and Green,
// "Ambient Occlusion", GPU Gems, 2004, ch. 17). A probe sees the sky along a direction where nothing in that
// direction's map lies between it and the sky, and keeps what it sees as an ambient cube, as Unity's Adaptive Probe
// Volumes keep a sky occlusion for each probe. A dense grid of probes near the camera is tested against the
// near maps, and a coarse one far out against the far maps. Each pixel reads the probes about it, the ones behind its
// surface left out. Past the probes and the far maps, the sky is open. The pass header's x is the frame's buffer and
// its y the buffer of SkyVisibilityConstants.
module "SkyVisibility";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Scene.ShadowViews;
import Light.Ambient;

// What the passes read beyond the frame, C#'s SkyVisibilityConstants, 144 bytes.
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
    // The world position of the far probes' first corner, and the metres between them.
    float3 probeOrigin;
    float probeCell;
    uint3 probeCounts;
    // The metres from the camera inside which the near probes alone decide, and past which the far ones alone do.
    float nearDistance;
    float farDistance;
    // Metres of depth that still count as the point itself, against the depth's error.
    float depthBias;
    // The first z of the slab of probes that this dispatch tests.
    uint probeFirstZ;
    // Which probes this dispatch tests: 0 the far ones against the far maps, 1 the near ones against the near maps.
    uint probeSet;
    // The world position of the near probes' first corner, and the metres between them.
    float3 nearOrigin;
    float nearCell;
    uint3 nearCounts;
    // The near probes' volume.
    uint nearProbes;
};

// The widest angle from the mirror direction, as a cosine, of the directions that the specular visibility counts.
static const float SpecularCone = 0.8660254;

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

// A probe keeps what it sees as an ambient cube (McTaggart, "Half-Life 2 / Valve Source Shading", GDC 2004): the
// cosine-weighted share of the sky seen by a surface facing each of the 6 axes. It takes 2 texels of its volume along
// x: the first holds +x, -x, +y and -y, the second +z and -z. The -y face sees no sky above the horizon, so it holds the
// share of the whole sky that the probe sees.
struct AmbientCube
{
    float4 first;
    float2 second;
};

static const AmbientCube OpenCube = { float4(1.0), float2(1.0) };

// The ambient cube's share for a direction, each face weighted by the square of the direction along its axis.
float EvaluateCube(AmbientCube cube, float3 d)
{
    float3 squared = d * d;
    return squared.x * (d.x >= 0.0 ? cube.first.x : cube.first.y) + squared.y * (d.y >= 0.0 ? cube.first.z
        : cube.first.w) + squared.z * (d.z >= 0.0 ? cube.second.x : cube.second.y);
}

// The probes of one volume: the world position of the first, the metres between them, how many there are, and their
// volume.
struct ProbeVolume
{
    float3 origin;
    float cell;
    uint3 counts;
    uint volume;
};

ProbeVolume GetNearVolume(SkyVisibilityConstants constants)
{
    ProbeVolume volume = { constants.nearOrigin, constants.nearCell, constants.nearCounts, constants.nearProbes };
    return volume;
}

ProbeVolume GetFarVolume(SkyVisibilityConstants constants)
{
    ProbeVolume volume = { constants.probeOrigin, constants.probeCell, constants.probeCounts, constants.probes };
    return volume;
}

// The ambient cube at a surface's point, read from the 8 probes about it as a trilinear filter does, each probe behind
// the surface's plane left out, so a wall's face does not read the probes on its other side: the normal-based leak
// reduction of Unity's Adaptive Probe Volumes. The point is moved off the surface by the sampling biases first. A
// plain trilinear read where every probe is behind, and false outside the volume.
bool TrySampleProbes(
    ProbeVolume probes,
    SamplerRef sampler,
    float3 surface,
    float3 sampled,
    float3 normal,
    out AmbientCube cube
)
{
    cube = OpenCube;
    float3 cell = (sampled - probes.origin) / probes.cell;
    float3 last = float3(probes.counts) - 1.0;
    if (any(cell < 0.0) || any(cell > last))
        return false;

    VolumeRef volume = { probes.volume };
    float3 size = float3(probes.counts) * float3(2.0, 1.0, 1.0);
    uint3 first = uint3(min(floor(cell), last - 1.0));
    float3 f = cell - float3(first);
    AmbientCube sum = { float4(0.0), float2(0.0) };
    AmbientCube plain = { float4(0.0), float2(0.0) };
    float weight = 0.0;
    for (uint i = 0u; i < 8u; i++)
    {
        uint3 corner = uint3(i & 1u, (i >> 1u) & 1u, i >> 2u);
        float3 along = lerp(1.0 - f, f, float3(corner));
        float trilinear = along.x * along.y * along.z;
        uint3 probe = first + corner;
        // A read at a texel's centre is that texel alone.
        float3 texel = float3(probe.x * 2u, probe.y, probe.z) + 0.5;
        float4 faces = volume.SampleLevel(sampler, texel / size, 0.0);
        float2 more = volume.SampleLevel(sampler, (texel + float3(1.0, 0.0, 0.0)) / size, 0.0).xy;
        float3 position = probes.origin + float3(probe) * probes.cell;
        float w = dot(position - surface, normal) > 0.0 ? trilinear : 0.0;
        sum.first += faces * w;
        sum.second += more * w;
        weight += w;
        plain.first += faces * trilinear;
        plain.second += more * trilinear;
    }

    if (weight > 1e-4)
    {
        cube.first = sum.first / weight;
        cube.second = sum.second / weight;
    }
    else
        cube = plain;

    return true;
}

// The share of the sky that a surface's diffuse light sees, and that its reflection sees: the cube along the normal,
// and along the mirror direction where it points to the sky.
float2 EvaluateProbes(AmbientCube cube, float3 normal, float3 mirror)
{
    float diffuse = saturate(EvaluateCube(cube, normal));
    float specular = mirror.y > 0.0 ? saturate(EvaluateCube(cube, mirror)) : diffuse;
    return float2(diffuse, specular);
}

// Each probe of a volume: what it sees along each direction of its maps, as an ambient cube. Each face is the mean of
// what is seen, weighted by the cosine to the face and by one over the directions' density: they are cosine-weighted,
// so the density goes as the cosine of the angle from the zenith.
[shader("compute")]
[numthreads(4, 4, 4)]
void TestSkyProbes(uint3 thread : SV_DispatchThreadID)
{
    SkyVisibilityConstants constants = LoadVisibilityConstants();
    bool isNear = constants.probeSet == 1u;
    ProbeVolume probes = isNear ? GetNearVolume(constants) : GetFarVolume(constants);
    uint3 probe = thread + uint3(0u, 0u, constants.probeFirstZ);
    if (any(probe >= probes.counts))
        return;

    uint maps = isNear ? constants.nearMaps : constants.farMaps;
    uint count = isNear ? constants.nearCount : constants.farCount;
    float3 point = probes.origin + float3(probe) * probes.cell - constants.anchor;
    TextureRef atlas = { isNear ? constants.nearAtlas : constants.farAtlas };
    SamplerRef sampler = { constants.sampler };
    // The faces +x, -x, +y, +z and -z, then the whole sky.
    float seen[6] = { 0.0, 0.0, 0.0, 0.0, 0.0, 0.0 };
    float total[6] = { 0.0, 0.0, 0.0, 0.0, 0.0, 0.0 };
    for (uint i = 0u; i < count; i++)
    {
        GpuShadowView view = LoadShadowView(maps, i);
        float visible = TestMap(view, atlas, sampler, point, constants.depthBias);
        if (visible < 0.0)
            continue;

        float3 d = view.towards;
        float density = 1.0 / max(d.y, 0.05);
        float facing[6] = { max(d.x, 0.0), max(-d.x, 0.0), max(d.y, 0.0), max(d.z, 0.0), max(-d.z, 0.0), 1.0 };
        for (uint face = 0u; face < 6u; face++)
        {
            float w = facing[face] * density;
            seen[face] += visible * w;
            total[face] += w;
        }
    }

    float share[6];
    for (uint face = 0u; face < 6u; face++)
        share[face] = total[face] > 1e-4 ? seen[face] / total[face] : 1.0;

    // A face that no direction faces takes the whole sky's share.
    for (uint face = 0u; face < 5u; face++)
        share[face] = total[face] > 1e-4 ? share[face] : share[5];

    StorageVolumeRef volume = { probes.volume, 0u };
    uint3 texel = uint3(probe.x * 2u, probe.y, probe.z);
    volume.Store(texel, float4(share[0], share[1], share[2], share[5]));
    volume.Store(texel + uint3(1u, 0u, 0u), float4(share[3], share[4], 0.0, 0.0));
}

// The share of the sky each pixel sees at half size, diffuse in red and specular in green: from the near probes inside
// the near distance, from the far probes past the far one or outside the near probes, and blended between. Each reads
// its probes from a point moved off the surface along the normal and towards the camera, the sampling biases of Unity's
// Adaptive Probe Volumes.
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
    SamplerRef sampler = { constants.sampler };

    float2 near = float2(1.0);
    bool hasNear = false;
    if (distance < constants.farDistance)
    {
        float3 at = world + normal * constants.normalOffset + view * (0.25 * constants.nearCell);
        AmbientCube cube;
        hasNear = TrySampleProbes(GetNearVolume(constants), sampler, world, at, normal, cube);
        near = EvaluateProbes(cube, normal, mirror);
    }

    float blend = hasNear
        ? saturate((distance - constants.nearDistance) / max(constants.farDistance - constants.nearDistance, Epsilon))
        : 1.0;
    float2 far = float2(1.0);
    if (blend > 0.0)
    {
        float3 at = world + normal * (0.5 * constants.probeCell) + view * (0.25 * constants.probeCell);
        AmbientCube cube;
        TrySampleProbes(GetFarVolume(constants), sampler, world, at, normal, cube);
        far = EvaluateProbes(cube, normal, mirror);
    }

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
