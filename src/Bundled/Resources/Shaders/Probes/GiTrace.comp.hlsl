// The probes' rays: a thread a ray of every probe due this frame (Lighting/Gi.hlsli's GiDueProbe) that lights anything
// (Probes/GiClassify), marched through the scene's field. A ray that hits takes the light its surface gives back: what it gives off, the sun as far
// as the field lets it through, and everything else as the probes held it last time, which is how light bounces again
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
    GiHit hit = GiTrace(origin, direction, index, header.levels, maxDistance);
    if (!hit.isHit)
    {
        STORE(GiRays, float4, thread.x, float4(SkyCube.SampleLevel(SkyCubeSampler, direction, 1.0).rgb, maxDistance));
        return;
    }

    if (hit.isInside)
    {
        STORE(GiRays, float4, thread.x, float4(0.0, 0.0, 0.0, 0.0));
        return;
    }

    GpuGiLevel hitLevel = GiLevel(hit.level);
    float3 normal = GiNormalAt(hitLevel, hit.level, hit.position);
    float3 uvw = (float3(GiStored(GiVoxelOf(hitLevel, hit.position), hit.level)) + 0.5) / float3(GiResolution, GiResolution, GiResolution * GiMaxLevels);
    float4 surface = GiSurface.SampleLevel(GiSurfaceSampler, uvw, 0.0);
    float3 albedo = surface.a > 0.0 ? surface.rgb : float3(0.5, 0.5, 0.5);
    float3 radiance = GiEmission.SampleLevel(GiEmissionSampler, uvw, 0.0).rgb;
    [branch] if (any(SunIlluminance > 0.0))
    {
        float facing = saturate(dot(normal, -SunDirection));
        [branch] if (facing > 0.0)
        {
            float visible = GiSunVisibility(hit.position + normal * hitLevel.voxel, -SunDirection, hit.level, header.levels);
            radiance += albedo * SunIlluminance * (facing * visible * InvPi);
        }
    }

    // What the probes held last time arriving there, the sky's where they hold nothing.
    float held;
    float3 bounced = GiIrradianceAt(hit.position, normal, normal, held);
    float3 sh[9];
    [unroll] for (uint i = 0u; i < 9u; i++)
    {
        sh[i] = LOAD(SkySh, float4, i).rgb;
    }

    radiance += albedo * lerp(ShIrradiance(normal, sh), bounced, held);

    // The field's surfaces stand out from the true ones by most of a voxel (Probes/GiJobs.hlsli), and a ray stops a
    // little short of those: the way to the true surface is that much further, which what the probe sees by needs.
    STORE(GiRays, float4, thread.x, float4(radiance, hit.distance + 0.95 * hitLevel.voxel));
}
