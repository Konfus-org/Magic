// The imposter: the cluster above a model's roots, a quad facing the camera around the instance's sphere, drawn with
// the instance's capture. The vertex entry and the resolve build the same quad from this module, so a pixel's place
// in it follows from its barycentrics.
module "Scene/Imposters";

import Core.Frame;
import Scene.Instances;

// A corner of the quad's two triangles in the camera-relative world, by the vertex's place in them, and where that
// corner is in the quad, from its top left.
public float3 GetImposterCorner(FrameConstants frame, GpuInstance instance, uint vertexIndex, out float2 quadUv)
{
    uint corner = vertexIndex % 6u;
    uint which = corner < 3u ? corner : (corner == 3u ? 0u : corner - 2u);
    quadUv = float2((which & 1u) != 0u ? 1.0 : 0.0, which >= 2u ? 1.0 : 0.0);
    if (which == 1u)
        quadUv = float2(1.0, 0.0);
    else if (which == 2u)
        quadUv = float2(1.0, 1.0);
    else if (which == 3u)
        quadUv = float2(0.0, 1.0);

    float3 right = float3(frame.view[0][0], frame.view[0][1], frame.view[0][2]);
    float3 up = float3(frame.view[1][0], frame.view[1][1], frame.view[1][2]);
    float3 center = Relative(frame, instance.boundsCenter);
    float2 offset = float2(quadUv.x * 2.0 - 1.0, 1.0 - quadUv.y * 2.0) * instance.boundsRadius;
    return center + right * offset.x + up * offset.y;
}

// Towards the camera from the instance, in the model's frame: which capture the instance shows.
public float3 GetImposterDirection(FrameConstants frame, GpuInstance instance)
{
    float3 world = normalize(frame.cameraPosition.xyz - instance.boundsCenter);
    float3x3 rotation = float3x3(instance.world[0].xyz, instance.world[1].xyz, instance.world[2].xyz);
    return normalize(mul(rotation, world));
}
