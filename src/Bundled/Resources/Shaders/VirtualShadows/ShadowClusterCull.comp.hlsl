// Culls the clusters of the shadows' jobs, a group a job and a thread a cluster, into pieces for the pages drawn this
// frame (ShadowPieces: an instance's slot, a cluster's row times four plus which piece, and the view drawing it). A
// cluster casts where its error, as the shadow's texels see it, is no more than ClusterError of them and its parent's
// is more, as the views' cut decides (Karis 2021); then into each page drawn whose rectangle drawn its bounds reach:
// the clipmap page its group is, or each face and mip of a light drawn, its pages under it looked up by key. A
// transparent cluster casts nothing; a masked one casts whole.

#include "Include/Culling.hlsli"
#include "Include/VirtualShadows.hlsli"

void Emit(uint slot, uint index, uint pieces, uint view)
{
    uint at = WaveAllocate(ShadowCounts, ShadowCountPieces, pieces);
    uint capacity = CountOf(ShadowPieces, 12u);
    [loop] for (uint piece = 0u; piece < pieces && at + piece < capacity; piece++)
    {
        ShadowPieces.Store3((at + piece) * 12u, uint3(slot, (index << 2) | piece, view));
    }
}

void CastLight(GpuClipmap clipmap, uint lightSlot, uint bits, float3 centre, float3 fromLight, float radius, uint views, uint slot, uint index, uint pieces)
{
    [loop] for (uint face = 0u; face < 6u; face++)
    {
        uint mips = (bits >> (face * LightMips)) & ((1u << LightMips) - 1u);
        if (mips == 0u)
        {
            continue;
        }

        // The rectangle the sphere covers on the face, 0 to 1 across it: all of it where the sphere reaches the light.
        float3 forward;
        float3 right;
        float3 up;
        FaceAxes(face, forward, right, up);
        float ahead = dot(fromLight, forward);
        if (ahead + radius <= LightNear)
        {
            continue;
        }

        float2 low = float2(0.0, 0.0);
        float2 high = float2(1.0, 1.0);
        if (ahead - radius > LightNear)
        {
            float2 across = float2(dot(fromLight, right), -dot(fromLight, up)) / ahead;
            float extent = radius / (ahead - radius);
            low = (across - extent) * 0.5 + 0.5;
            high = (across + extent) * 0.5 + 0.5;
            if (any(high < 0.0) || any(low > 1.0))
            {
                continue;
            }
        }

        [loop] while (mips != 0u)
        {
            uint mip = firstbitlow(mips);
            mips &= mips - 1u;
            uint side = FacePages(mip);
            uint2 first = (uint2)(saturate(low) * side);
            uint2 last = min((uint2)(saturate(high) * side), side - 1u);
            [loop] for (uint y = first.y; y <= last.y; y++)
            {
                [loop] for (uint x = first.x; x <= last.x; x++)
                {
                    uint found = FindLightEntry(ShadowTables, clipmap.lightTable, LightKeyOf(lightSlot, face, mip, uint2(x, y)), clipmap.lightEntries);
                    if (found == NoView)
                    {
                        continue;
                    }

                    uint view = ShadowTables.Load((clipmap.lightTable + found) * 16u + 12u);
                    if (view < views && ReachesViewRect(LOAD(ShadowViews, GpuShadowView, view), centre, radius))
                    {
                        Emit(slot, index, pieces, view);
                    }
                }
            }
        }
    }
}

[numthreads(64, 1, 1)]
void main(uint3 group : SV_GroupID, uint thread : SV_GroupIndex)
{
    uint job = LinearGroup(group);
    if (job >= min(ShadowCounts.Load(ShadowCountJobs), CountOf(ShadowJobs, 16u)))
    {
        return;
    }

    uint4 work = ShadowJobs.Load4(job * 16u);
    if (thread >= work.z)
    {
        return;
    }

    uint slot = work.x;
    uint index = work.y + thread;
    GpuInstance instance = LOAD(Instances, GpuInstance, slot);
    GpuCluster cluster = LOAD(Clusters, GpuCluster, index);
    uint row = InstanceMaterial(instance, ClusterSlot(cluster));
    if (row == 0xFFFFu)
    {
        return;
    }

    if (!IsDrawnOpaque(MaterialBins.Load(row * 4u)))
    {
        return;
    }

    GpuClipmap clipmap = ClipmapOf();
    GpuShadowGroup target = LOAD(ShadowGroups, GpuShadowGroup, work.w);
    uint views = ShadowCounts.Load(ShadowCountViews);
    float3 centre = InstancePosition(instance, cluster.bounds.xyz);
    float radius = cluster.bounds.w * instance.maxScale;
    uint pieces = ClusterPieces(cluster);
    if ((target.target & LightOwner) == 0u)
    {
        // A clipmap level's texels are one size: the cut is the same everywhere on it.
        float perMetre = instance.maxScale / target.texel;
        if (cluster.error * perMetre > ClusterError || cluster.parentError * perMetre <= ClusterError)
        {
            return;
        }

        if (ReachesViewRect(LOAD(ShadowViews, GpuShadowView, target.view), centre, radius))
        {
            Emit(slot, index, pieces, target.view);
        }

        return;
    }

    // A light's texels grow with distance from it: a face's finest mip drawn sets how many pixels an error is.
    float perMetre = instance.maxScale * (float)FaceTexels(min(FinestMip(target.faceMips), LightMips - 1u)) * 0.5;
    float own = max(length(InstancePosition(instance, cluster.errorBounds.xyz) - target.light) - cluster.errorBounds.w * instance.maxScale, LightNear);
    float parent = max(length(InstancePosition(instance, cluster.parentErrorBounds.xyz) - target.light) - cluster.parentErrorBounds.w * instance.maxScale, LightNear);
    if (cluster.error * perMetre / own > ClusterError || cluster.parentError * perMetre / parent <= ClusterError)
    {
        return;
    }

    CastLight(clipmap, target.target & ~LightOwner, target.faceMips, centre, centre - target.light, radius, views, slot, index, pieces);
}
