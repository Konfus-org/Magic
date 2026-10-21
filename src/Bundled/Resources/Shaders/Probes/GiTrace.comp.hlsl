// The probes' rays: a thread a ray of every probe due this frame (Lighting/Gi.hlsli's GiDueProbe) that lights anything
// (Probes/GiClassify), marched through the scene's field. A ray that hits takes the light its surface gives back: what it gives off, the sun and the
// lights near it as far as the field lets them through, and everything else as the probes held it last time, which is how light bounces again
// and again; one that misses takes the sky's. Written with how far it went, or 0 for one that starts inside something.

#include "Lighting/Gi.hlsli"
#include "Lighting/Ibl.hlsli"

[numthreads(64, 1, 1)]
void main(uint3 thread : SV_DispatchThreadID)
{
    GpuGi header = GiHeader();
    uint slot = thread.x / header.rays;
    uint ray = thread.x % header.rays;
    uint probeIndex;
    if (!GiDueProbe(header, slot, probeIndex) || thread.x >= CountOf(GiRays, 16u) || (LOAD(GiProbeState, int4, probeIndex).w & GiProbeUsed) == 0)
    {
        return;
    }

    uint index;
    GpuGiLevel level;
    int3 probe = GiProbeOf(probeIndex, index, level);
    float3 origin = GiProbePosition(level, probe);
    float3 direction = GiRayDirection(ray, header.rays, header.seed);
    float maxDistance = GiLevel(header.levels - 1u).voxel * (float)GiResolution * 0.5;
    GiHit hit = GiTrace(origin, direction, index, header.levels, maxDistance, 0.2, false);
    if (!hit.isHit)
    {
        STORE(GiRays, float4, thread.x, float4(SkyCube.SampleLevel(SkyCubeSampler, direction, 2.0).rgb, maxDistance));
        return;
    }

    if (hit.isInside)
    {
        STORE(GiRays, float4, thread.x, float4(0.0, 0.0, 0.0, 0.0));
        return;
    }

    float3 sh[9];
    [unroll] for (uint i = 0u; i < 9u; i++)
    {
        sh[i] = LOAD(SkySh, float4, i).rgb;
    }

    // The field's surfaces stand out from the true ones by most of a voxel (Probes/GiJobs.hlsli), and a ray stops a
    // little short of those: the way to the true surface is that much further, which what the probe sees by needs.
    STORE(GiRays, float4, thread.x, float4(GiHitRadiance(hit, header.levels, sh), hit.distance + 0.95 * GiLevel(hit.level).voxel));
}
