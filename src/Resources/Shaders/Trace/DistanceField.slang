// Rays through the global distance field: sphere tracing, each step as long as the distance to the nearest surface
// allows, with the cone's visibility narrowed by how close the surface came (Quilez, "Soft shadows in raymarched
// SDFs", 2010). Its constants come from the buffer whose heap id is the pass header's w.
module "Trace/DistanceField";

import Core.Heap;
import Core.Pass;
import Core.Math;
import Scene.Field;
import Trace.IWorldTracer;

public FieldConstants GetFieldConstants() { return LoadConstants<FieldConstants>(GetPassHeader().w); }

public struct DistanceFieldTracer : IWorldTracer
{
    public static TraceHit Trace(Ray ray, TraceLimits limits)
    {
        FieldConstants field = GetFieldConstants();
        TraceHit hit;
        hit.isHit = false;
        hit.distance = limits.end;
        hit.normal = float3(0.0, 1.0, 0.0);
        hit.visibility = 1.0;
        hit.instance = TraceNoInstance;
        float t = limits.start;
        for (uint i = 0u; i < 64u && t < limits.end; i++)
        {
            float3 point = ray.origin + ray.direction * t;
            uint level;
            float distance = SampleField(field, point, level);
            float voxel = field.voxelSizes[level];
            if (distance < voxel * 0.5)
            {
                hit.isHit = true;
                hit.distance = t;
                hit.normal = SampleFieldNormal(field, point);
                hit.visibility = 0.0;
                return hit;
            }

            hit.visibility = min(hit.visibility, distance / max(t * limits.coneTangent + voxel * 0.5, Epsilon));
            t += max(distance, voxel * 0.5);
        }

        return hit;
    }
}
