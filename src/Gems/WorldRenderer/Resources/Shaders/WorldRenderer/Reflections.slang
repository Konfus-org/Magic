// The sharp reflections: a ray a pixel, or one for every two pixels across, drawn from the GGX lobe of the surface,
// along the screen, then through the field, then into the world probes; the rays gathered for every pixel over a
// reach the roughness sets, each weighed by how near its surface and what it hit are to the pixel's own; and that
// blended with last frame's, reprojected, clamped to what this frame saw nearby. The pass header's x is the
// frame's constants, y the shadow constants, z the lighting constants and w the field constants.
module "WorldRenderer/Reflections";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Core.Pack;
import Core.Random;
import Core.Sampling;
import Scene.Lighting;
import Scene.Field;
import Scene.Probes;
import Scene.SurfaceCache;
import Scene.Translucency;
import Surface.Brdf;
import Sky.ISky;
import Trace.IWorldTracer;
import Trace.DistanceField;

static const float HistoryWeight = 0.92;

// The surface drawn at a pixel, as the reflections need it: false where nothing or something too rough is drawn.
bool LoadSurface(FrameConstants frame, uint2 pixel, out float3 world, out float3 normal, out float roughness, out float depth)
{
    world = float3(0.0);
    normal = float3(0.0, 1.0, 0.0);
    roughness = 1.0;
    depth = 0.0;
    UintTextureRef visibility = { frame.visibility };
    if (visibility.Load(pixel, 0u) == 0u)
        return false;

    TextureRef surfaceTexture = { frame.gbufferSurface };
    roughness = surfaceTexture.Load(pixel, 0u).x;
    if (roughness > ReflectionRoughnessLimit)
        return false;

    TextureRef depthTexture = { frame.depth };
    TextureRef normalTexture = { frame.gbufferNormal };
    depth = depthTexture.Load(pixel, 0u).x;
    world = ReconstructWorld(frame, GetTexelCenterUv(pixel, uint2(frame.width, frame.height)), depth);
    normal = DecodeOctahedral(normalTexture.Load(pixel, 0u).xy * 2.0 - 1.0);
    return true;
}

// Each texel of the traced table traces one ray for one of the pixels it stands for, a different one each frame.
[shader("compute")]
[numthreads(8, 8, 1)]
void ReflectionTrace<S : ISky, T : IWorldTracer>(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    LightingConstants lighting = GetLighting();
    GiConstants gi = LoadConstants<GiConstants>(lighting.gi);
    FieldConstants field = GetFieldConstants();
    StorageRef traced = { gi.reflectionTraced, 0u };
    uint2 size = traced.GetSize();
    if (any(thread.xy >= size))
        return;

    uint2 pixel = min((thread.xy * gi.reflectionStride) + GetStridePixel(gi), uint2(frame.width, frame.height) - 1u);
    float3 world;
    float3 normal;
    float roughness;
    float depth;
    if (!LoadSurface(frame, pixel, world, normal, roughness, depth))
    {
        traced.Store(thread.xy, float4(0.0, 0.0, 0.0, -1.0));
        return;
    }

    float3 viewDirection = NormalizeSafe(frame.cameraPosition.xyz - world, normal);
    float3x3 basis = CreateBasis(normal);
    float2 xi = GetRandom(pixel, gi.frameIndex, 7u).xy;
    float3 half = ToWorld(basis, SampleGgxVisibleNormal(ToLocal(basis, viewDirection), max(roughness * roughness, 0.002), xi));
    float3 direction = reflect(-viewDirection, half);
    if (dot(direction, normal) <= 0.0)
        direction = reflect(-viewDirection, normal);

    uint level;
    SampleField(field, world, level);
    float start = field.voxelSizes[level] * 1.5;
    float3 origin = world + normal * start;
    float3 radiance;
    float hitDistance;
    if (!TraceScreenToSurface(frame, gi, world + normal * 0.02, direction, radiance, hitDistance))
    {
        Ray ray = { origin, direction };
        TraceLimits limits = { start, gi.rayRange, 0.0 };
        TraceHit hit = T.Trace(ray, limits);
        hitDistance = hit.distance;
        if (hit.isHit)
        {
            radiance = ShadeFieldHit(frame, field, gi, origin + direction * hit.distance, hit.normal, hit.instance);
        }
        else if (!SampleWorldProbeRadiance(gi, origin + direction * min(hit.distance, gi.rayRange), direction, radiance))
        {
            SkyInputs sky;
            sky.viewDirection = direction;
            sky.sunDirection = frame.sunDirection.xyz;
            sky.hasSunDisc = false;
            sky.sunIlluminance = lighting.sunIlluminance.rgb;
            sky.altitude = lighting.cameraAltitude.x;
            sky.transmittanceLut = lighting.transmittanceLut;
            sky.multiScatteringLut = lighting.multiScatteringLut;
            sky.skyViewLut = lighting.skyViewLut;
            sky.sampler = lighting.lutSampler;
            radiance = lighting.hasSky != 0u ? S.Evaluate(sky) : frame.skyRadiance.rgb * lighting.sunIlluminance.r;
        }
    }

    radiance *= GetGlassTransmittance(GetTranslucency(), origin, origin + direction * min(hitDistance, gi.rayRange));
    traced.Store(thread.xy, float4(radiance, hitDistance));
}

// The pixel of each stride's square a ray is traced for this frame.
uint2 GetStridePixel(GiConstants gi)
{
    return gi.reflectionStride == 1u ? uint2(0u) : uint2(gi.frameIndex & 1u, (gi.frameIndex >> 1u) & 1u);
}

// Each pixel gathers the rays traced around it, over a reach that grows with its roughness, none for a mirror,
// weighed by how close their surfaces are to its own and how near what they hit is to what its own ray hit, so a
// mirror stays sharp and a rough floor smooth.
[shader("compute")]
[numthreads(8, 8, 1)]
void ReflectionResolve(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    LightingConstants lighting = GetLighting();
    GiConstants gi = LoadConstants<GiConstants>(lighting.gi);
    uint2 size = uint2(frame.width, frame.height);
    if (any(thread.xy >= size))
        return;

    StorageRef resolved = { gi.reflectionResolved, 0u };
    float3 world;
    float3 normal;
    float roughness;
    float depth;
    if (!LoadSurface(frame, thread.xy, world, normal, roughness, depth))
    {
        resolved.Store(thread.xy, float4(0.0));
        return;
    }

    TextureRef traced = { gi.reflectionTraced };
    TextureRef depthTexture = { frame.depth };
    int2 tracedSize = int2(traced.GetSize(0u));
    int2 center = clamp(int2(thread.xy / gi.reflectionStride), int2(0), tracedSize - 1);
    int reach = int(round(roughness * 6.0));
    float viewDepth = ToViewDepth(depth, frame.near);
    float4 own = traced.Load(uint2(center), 0u);
    float4 sum = float4(0.0);
    float weightSum = 0.0;
    for (int y = -reach; y <= reach; y++)
    {
        for (int x = -reach; x <= reach; x++)
        {
            int2 texel = center + int2(x, y);
            if (any(texel < 0) || any(texel >= tracedSize))
                continue;

            float4 sample = traced.Load(uint2(texel), 0u);
            if (sample.w < 0.0)
                continue;

            // The pixel the ray was traced for, this frame: how far its surface is from this one, and how far what
            // its ray hit is from what this pixel's own ray hit.
            uint2 sourcePixel = min((uint2(texel) * gi.reflectionStride) + GetStridePixel(gi), size - 1u);
            float sourceDepth = ToViewDepth(depthTexture.Load(sourcePixel, 0u).x, frame.near);
            float closeness = saturate(1.0 - abs(sourceDepth - viewDepth) / (0.05 * viewDepth + 0.05));
            float sameHit = own.w < 0.0 ? 1.0 : saturate(1.0 - abs(sample.w - own.w) / (0.5 * own.w + 0.1));
            float falloff = exp(-float(x * x + y * y) / float(reach * reach + 1));
            float weight = closeness * lerp(sameHit, 1.0, roughness) * falloff;
            sum += sample * weight;
            weightSum += weight;
        }
    }

    resolved.Store(thread.xy, weightSum > 1e-4 ? sum / weightSum : float4(0.0));
}

// Each pixel's reflection blended with last frame's at the same surface, found by the surface's velocity, held
// within what its neighbours see now.
[shader("compute")]
[numthreads(8, 8, 1)]
void ReflectionTemporal(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    LightingConstants lighting = GetLighting();
    GiConstants gi = LoadConstants<GiConstants>(lighting.gi);
    uint2 size = uint2(frame.width, frame.height);
    if (any(thread.xy >= size))
        return;

    StorageRef output = { gi.reflections, 0u };
    TextureRef resolved = { gi.reflectionResolved };
    float3 world;
    float3 normal;
    float roughness;
    float depth;
    if (!LoadSurface(frame, thread.xy, world, normal, roughness, depth))
    {
        output.Store(thread.xy, float4(0.0));
        return;
    }

    float4 current = resolved.Load(thread.xy, 0u);
    float viewDepth = ToViewDepth(depth, frame.near);
    float3 result = current.rgb;
    TextureRef velocityTexture = { frame.velocity };
    float3 velocity = velocityTexture.Load(thread.xy, 0u).xyz;
    float2 previousUv = GetTexelCenterUv(thread.xy, size) + velocity.xy;
    if (all(previousUv >= 0.0) && all(previousUv < 1.0))
    {
        TextureRef history = { gi.reflectionsPrevious };
        SamplerRef sampler = { frame.linearSampler };
        float4 held = history.SampleLevel(sampler, previousUv, 0.0);
        if (held.w > 0.0 && abs(held.w - velocity.z) < 0.05 * velocity.z)
        {
            float3 low = current.rgb;
            float3 high = current.rgb;
            for (int y = -1; y <= 1; y++)
            {
                for (int x = -1; x <= 1; x++)
                {
                    uint2 texel = uint2(clamp(int2(thread.xy) + int2(x, y), int2(0), int2(size) - 1));
                    float3 near = resolved.Load(texel, 0u).rgb;
                    low = min(low, near);
                    high = max(high, near);
                }
            }

            result = lerp(current.rgb, clamp(held.rgb, low, high), HistoryWeight);
        }
    }

    output.Store(thread.xy, float4(result, viewDepth));
}
