// The light that reaches each surface indirectly, traced at a part of the picture's size: from one pixel of each traced
// pixel's footprint, a few rays over its hemisphere as the diffuse lobe weighs it, each bringing back the light of the
// surface it lands on, lit by the sun, the lights near it and the probes, or where it reaches nothing within range, the
// sky's light if nothing stands beyond, else the probes' light at the pixel. The trace writes NVIDIA's NRD's inputs,
// and NRD denoises the light; shading reads NRD's output through Light/Indirect. The share of the rays that reach the
// sky, the sky the surface sees, is blended over frames beside the view depth. Adapted from Wicked Engine's ray-traced
// diffuse.
// The pass header's x is the frame's buffer, which holds the lights and the indirect light. Its y is the GI buffer.
module "TraceDiffuse";

import Core.Color;
import Core.Heap;
import Core.Frame;
import Core.Math;
import Core.Random;
import Core.Sampling;
import Scene.Lights;
import Light.Probes;
import Light.Indirect;
import Traced;
import Sky.ISky;
import Trace.HitSurface;
import Scene.Models;
import Nrd;

// The view depth NRD takes for a pixel with nothing drawn: past its denoising range, so it leaves the pixel out.
static const float NoSurfaceViewZ = 1e7;

// How many times brighter than the probes' light at the pixel NRD's input may be: brighter, a few rays that found a
// small bright patch are a firefly that NRD spreads over frames. PROTO: the 12 is a guess.
static const float FireflyRatio = 12.0;

// The hit distance NRD takes for a ray that reaches nothing: the most a half float holds.
static const float MissDistance = 65504.0;

[shader("compute")]
[numthreads(8, 8, 1)]
void TraceDiffuse<S : ISky>(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    GIConstants gi = GetGIConstants();
    TracedDiffuseConstants diffuse = gi.diffuse;
    if (any(thread.xy >= gi.tracedSize))
        return;

    uint2 pixel = GetTracedPixel(frame, thread.xy, gi.tracedSize);
    StorageRef traced = { diffuse.traced, 0u };
    TextureRef depthTexture = { frame.depth };
    float depth = depthTexture.Load(pixel, 0u).x;
    StorageRef radiance = { diffuse.radiance, 0u };
    StorageRef viewZ = { diffuse.viewZ, 0u };
    if (depth == 0.0)
    {
        traced.Store(thread.xy, float4(0.0));
        radiance.Store(thread.xy, float4(0.0));
        viewZ.Store(thread.xy, float4(NoSurfaceViewZ));
        return;
    }

    Lights lights = GetLights();
    ProbeGridConstants grid = GetProbeGrid();
    float3 normal = LoadNormal(frame.gbufferNormal, pixel);
    float3 relative = ReconstructPixel(frame, pixel, depth);
    float3 world = relative + frame.cameraPosition.xyz;
    float3 origin = world + normal * GetRayOffset(frame, relative);
    float3 beyond = GetProbeIrradiance(grid, world, normal);
    float3x3 basis = CreateBasis(normal);
    RaytracingAccelerationStructure scene = GetTracedAcceleration();
    float3 sum = float3(0.0);
    float sky = 0.0;
    float hitSum = 0.0;
    uint hitCount = 0u;
    for (uint i = 0u; i < gi.diffuseRayCount; i++)
    {
        float3 direction = ToWorld(basis, SampleCosineHemisphere(GetRandom(thread.xy, frame.frameIndex, 1u + i).xy));
        // A ray that lands on a back face is inside a mesh, as a ray from the seam between two logs is: it brings back
        // no light. Skipped, it passed out through the wall and brought the sky in, a pale line along each seam.
        RayHit hit = GetClosestHit(scene, origin, direction, gi.diffuseRayRange, RAY_FLAG_NONE);
        if (hit.isHit && !hit.isFrontFace)
            continue;

        // Past the reach, the sky's light along the ray where nothing stands beyond, as the probes' rays bring back, so
        // a ray out of a window brings the sky in, not the room's light.
        if (!hit.isHit)
        {
            float3 start = origin + direction * gi.diffuseRayRange;
            float3 open = hit.through * GetTransmittance(scene, start, direction, 1e30);
            float3 skyLight = (gi.diffuseTermsOff & 2u) == 0u ? GetSkyRadiance<S>(lights, direction) : float3(0.0);
            sum += any(open > 0.0) ? skyLight * open : beyond * hit.through;
            sky += GetLuminance(open);
            continue;
        }

        HitSurface surface = LoadHitSurface(hit, origin, direction);
        float3 xi = GetRandom(thread.xy, frame.frameIndex, 1u + gi.diffuseRayCount + i);
        float3 hitLight = (gi.diffuseTermsOff & 1u) == 0u
            ? GetHitLight<S>(lights, grid, scene, surface, xi)
            : GetDirectLight<S>(lights, grid, scene, surface, xi) * surface.diffuse + surface.emissive;
        sum += hitLight * hit.through;
        hitSum += hit.distance;
        hitCount++;
    }

    // NRD's inputs: the light and its rays' mean hit distance, the motion to where the surface was last frame in the
    // picture's UV, the normal from 0 to 1 with a roughness of 1, and the view depth.
    float count = float(gi.diffuseRayCount);
    traced.Store(thread.xy, float4(sum, sky) / count);
    float3 mean = sum / count;
    float limit = FireflyRatio * GetLuminance(beyond);
    float brightness = GetLuminance(mean);
    if (limit > 0.0 && brightness > limit)
        mean *= limit / brightness;
    // Capped, so no value past a half float's range, which is infinite, enters NRD's history.
    radiance.Store(thread.xy, float4(min(mean * NrdRadianceScale, 250.0), hitCount > 0u ? hitSum / float(hitCount) : MissDistance));
    TextureRef velocityTexture = { frame.velocity };
    StorageRef motion = { diffuse.motion, 0u };
    motion.Store(thread.xy, float4(velocityTexture.Load(pixel, 0u).xy, 0.0, 0.0));
    StorageRef normalRoughness = { diffuse.normalRoughness, 0u };
    normalRoughness.Store(thread.xy, NRD_FrontEnd_PackNormalAndRoughness(normal, 1.0, 0.0));
    viewZ.Store(thread.xy, float4(ToViewDepth(depth, frame.near)));
}

// The share of a traced pixel's rays that reached the sky this frame, in the trace's alpha, averaged over the 3 by 3
// traced pixels around it whose view depths are within a tenth of its own.
float LoadSkyShare(FrameConstants frame, TextureRef traced, uint2 at, uint2 size, float viewDepth)
{
    TextureRef depthTexture = { frame.depth };
    float sum = 0.0;
    float weightSum = 0.0;
    for (uint i = 0u; i < 9u; i++)
    {
        uint2 neighbour = uint2(clamp(int2(at) + int2(i % 3u, i / 3u) - 1, int2(0), int2(size) - 1));
        float depth = depthTexture.Load(GetPicturePixel(frame, neighbour, size, float2(0.0)), 0u).x;
        float gap = abs(viewDepth - ToViewDepth(depth, frame.near)) / viewDepth;
        float weight = depth > 0.0 ? 1.0 - saturate(gap * 10.0) : 0.0;
        sum += traced.Load(neighbour, 0u).a * weight;
        weightSum += weight;
    }

    return sum / max(weightSum, Epsilon);
}

// A thread a traced pixel: the share of the sky it sees blended with the history's where the texel there saw the same
// surface, kept in y beside the view depth for the next frame's history, and the frames it has blended in z. A pixel
// new to the view starts from its own share and blends by 1 over its frames, up to the history's blend, so it settles
// in a few frames, not in 1 over 1 minus the blend.
[shader("compute")]
[numthreads(8, 8, 1)]
void BlendSkyShare(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    GIConstants gi = GetGIConstants();
    uint2 size = gi.tracedSize;
    if (any(thread.xy >= size))
        return;

    uint2 pixel = GetPicturePixel(frame, thread.xy, size, float2(0.0));
    TextureRef depthTexture = { frame.depth };
    float depth = depthTexture.Load(pixel, 0u).x;
    StorageRef kept = { gi.depth, 0u };
    if (depth == 0.0)
    {
        kept.Store(thread.xy, float4(0.0));
        return;
    }

    float viewDepth = ToViewDepth(depth, frame.near);
    TextureRef traced = { gi.diffuse.traced };
    float sky = LoadSkyShare(frame, traced, thread.xy, size, viewDepth);
    float frames = 1.0;
    if (gi.diffuse.hasHistory != 0u)
    {
        TextureRef velocityTexture = { frame.velocity };
        float3 velocity = velocityTexture.Load(pixel, 0u).xyz;
        float2 uv = GetTexelCenterUv(thread.xy, size) + velocity.xy;
        TextureRef depthHistory = { gi.depthHistory };
        float3 history = depthHistory.Load(GetTexelAt(saturate(uv), size), 0u).xyz;
        if (GetSameness(velocity.z, history.x) >= SameSurface && all(saturate(uv) == uv))
        {
            frames = min(history.z + 1.0, 1000.0);
            sky = lerp(sky, history.y, min(gi.diffuseHistory, 1.0 - 1.0 / frames));
        }
    }

    kept.Store(thread.xy, float4(viewDepth, sky, frames, 0.0));
}
