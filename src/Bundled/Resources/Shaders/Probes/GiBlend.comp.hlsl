// The probes traced this frame take in their rays: a group a probe, and a thread for each texel inside its tiles, its
// distance tile's then its irradiance tile's, which every thread then writes out, a texel of the distance tile each and
// the first 64 one of the irradiance tile as well, a border texel the inside one across the octahedron's fold.
// Irradiance is the rays' light weighted by the cosine to the texel's direction, distance the rays' lengths and their
// squares by a sharp lobe around it (the cosine to the 50th); each blended into what the probe held (GiHistory) by its
// level's hysteresis, unless it holds nothing yet. A probe given the next coarser level's light until it was traced
// (Probes/GiClassify) blends its first tracing into that at least halfway, and takes what it sees whole. A probe that
// lights nothing this frame is left as it is.

#include "Lighting/Gi.hlsli"

// The probe's rays and their directions, read once for the group rather than by each texel; as many as a probe has.
groupshared float4 SharedRays[256];
groupshared float3 SharedDirections[256];

// What the texels inside its tiles came to, which the border texels copy.
groupshared float2 SharedMoments[GiDistanceInside * GiDistanceInside];
groupshared float3 SharedIrradiance[GiIrradianceInside * GiIrradianceInside];

[numthreads(256, 1, 1)]
void main(uint3 group : SV_GroupID, uint thread : SV_GroupIndex)
{
    GpuGi header = GiHeader();
    uint probeIndex;
    if (!GiDueProbe(header, group.x, probeIndex))
    {
        return;
    }

    int4 state = LOAD(GiProbeState, int4, probeIndex);
    if ((state.w & GiProbeUsed) == 0)
    {
        return;
    }

    uint index;
    GpuGiLevel level;
    int3 probe = GiProbeOf(probeIndex, index, level);
    bool isTraced = (state.w & GiProbeHeld) != 0;
    bool isSeeded = (state.w & GiProbeSeeded) != 0;
    for (uint loaded = thread; loaded < header.rays; loaded += 256u)
    {
        SharedRays[loaded] = LOAD(GiRays, float4, group.x * header.rays + loaded);
        SharedDirections[loaded] = GiRayDirection(loaded, header.rays, header.seed);
    }

    GroupMemoryBarrierWithGroupSync();

    if (thread < GiDistanceInside * GiDistanceInside)
    {
        float maxDistance = GiMaxSpacings * (float)GiStride * level.voxel;
        float3 direction = GiTexelDirection(uint2(1u + thread % GiDistanceInside, 1u + thread / GiDistanceInside), GiDistanceSide);
        float2 moments = float2(0.0, 0.0);
        float weights = 0.0;
        [loop] for (uint ray = 0u; ray < header.rays; ray++)
        {
            float cosine = saturate(dot(direction, SharedDirections[ray]));
            float square = cosine * cosine;
            float sixteenth = square * square;
            sixteenth *= sixteenth;
            sixteenth *= sixteenth;
            float lobe = sixteenth * sixteenth * sixteenth * square;
            float distance = min(SharedRays[ray].w, maxDistance);
            moments += lobe * float2(distance, distance * distance);
            weights += lobe;
        }

        moments /= max(weights, 1e-6);
        uint slot = GiDistanceHistory(probeIndex, thread);
        if (isTraced)
        {
            moments = lerp(moments, GiUnpackHalf(uint2(GiHistory.Load(slot * 4u), 0u)).xy, level.hysteresis);
        }

        GiHistory.Store(slot * 4u, GiPackHalf(float4(moments, 0.0, 0.0)).x);
        SharedMoments[thread] = moments;
    }
    else if (thread < GiDistanceInside * GiDistanceInside + GiIrradianceInside * GiIrradianceInside)
    {
        uint inside = thread - GiDistanceInside * GiDistanceInside;
        float3 direction = GiTexelDirection(uint2(1u + inside % GiIrradianceInside, 1u + inside / GiIrradianceInside), GiIrradianceSide);
        float3 irradiance = float3(0.0, 0.0, 0.0);
        float weights = 0.0;
        [loop] for (uint ray = 0u; ray < header.rays; ray++)
        {
            float cosine = saturate(dot(direction, SharedDirections[ray]));
            irradiance += cosine * SharedRays[ray].rgb;
            weights += cosine;
        }

        irradiance *= GiStoreScale / max(weights, 1e-6);
        uint slot = GiIrradianceHistory(probeIndex, inside);
        if (isTraced || isSeeded)
        {
            irradiance = lerp(irradiance, GiUnpackHalf(GiHistory.Load2(slot * 4u)).rgb, isTraced ? level.hysteresis : min(level.hysteresis, 0.5));
        }

        GiHistory.Store2(slot * 4u, GiPackHalf(float4(irradiance, 0.0)));
        SharedIrradiance[inside] = irradiance;
    }

    GroupMemoryBarrierWithGroupSync();

    uint2 tile = GiTile(probe, index);
    uint2 distanceAt = uint2(thread % GiDistanceSide, thread / GiDistanceSide);
    uint2 distanceSource = GiBorderSource(distanceAt, GiDistanceSide) - 1u;
    GiDistance[tile * GiDistanceSide + distanceAt] = SharedMoments[distanceSource.y * GiDistanceInside + distanceSource.x];
    if (thread < GiIrradianceSide * GiIrradianceSide)
    {
        uint2 irradianceAt = uint2(thread % GiIrradianceSide, thread / GiIrradianceSide);
        uint2 irradianceSource = GiBorderSource(irradianceAt, GiIrradianceSide) - 1u;
        GiIrradiance[tile * GiIrradianceSide + irradianceAt] = float4(SharedIrradiance[irradianceSource.y * GiIrradianceInside + irradianceSource.x], 1.0);
    }

    if (thread == 0u)
    {
        STORE(GiProbeState, int4, probeIndex, int4(probe, GiProbeUsed | GiProbeHeld));
    }
}
