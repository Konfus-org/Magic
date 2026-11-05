// Rays through the GPU's own acceleration structures, on the GPUs that have them: a ray query against the top-level
// structure over the instances' leaf clusters, which lands exactly on the triangles the picture is drawn from. Its
// structure and the leaf cluster table come from the frame's constants.
module "Trace/RayQuery";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Scene.Instances;
import Scene.Clusters;
import Scene.Vertices;
import Trace.IWorldTracer;

public struct RayQueryTracer : IWorldTracer
{
    public static TraceHit Trace(Ray ray, TraceLimits limits)
    {
        FrameConstants frame = GetFrame();
        TraceHit hit;
        hit.isHit = false;
        hit.distance = limits.end;
        hit.normal = float3(0.0, 1.0, 0.0);
        hit.visibility = 1.0;
        hit.instance = TraceNoInstance;
        if (frame.acceleration == 0u || limits.end <= limits.start)
            return hit;

        RayDesc desc;
        desc.Origin = ray.origin;
        desc.Direction = ray.direction;
        desc.TMin = limits.start;
        desc.TMax = limits.end;
        AccelerationRef acceleration = { frame.acceleration };
        RayQuery<RAY_FLAG_FORCE_OPAQUE | RAY_FLAG_SKIP_PROCEDURAL_PRIMITIVES> query;
        query.TraceRayInline(acceleration.Get(), RAY_FLAG_NONE, 0xFFu, desc);
        query.Proceed();
        if (query.CommittedStatus() != COMMITTED_TRIANGLE_HIT)
            return hit;

        // The triangle: the instance's leaf cluster the geometry index names, and the primitive in it.
        uint instanceIndex = query.CommittedInstanceID();
        uint leafOffset = query.CommittedInstanceContributionToHitGroupIndex();
        ArrayRef<uint> leaves = { frame.leafClusters };
        uint clusterIndex = leaves[leafOffset + query.CommittedGeometryIndex()];
        GpuInstance instance = LoadInstance(frame.instances, instanceIndex);
        GpuCluster cluster = LoadCluster(frame.clusters, clusterIndex);
        uint first = cluster.indexOffset + query.CommittedPrimitiveIndex() * 3u;
        float3 p0 = ToWorld(instance.world, LoadVertex(frame.vertices, LoadIndex(frame.indices, first)).position);
        float3 p1 = ToWorld(instance.world, LoadVertex(frame.vertices, LoadIndex(frame.indices, first + 1u)).position);
        float3 p2 = ToWorld(instance.world, LoadVertex(frame.vertices, LoadIndex(frame.indices, first + 2u)).position);
        float3 normal = NormalizeSafe(cross(p1 - p0, p2 - p0), float3(0.0, 1.0, 0.0));
        hit.isHit = true;
        hit.distance = query.CommittedRayT();
        hit.normal = dot(normal, ray.direction) > 0.0 ? -normal : normal;
        hit.visibility = 0.0;
        hit.instance = instanceIndex;
        return hit;
    }
}
