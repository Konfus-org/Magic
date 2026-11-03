// The visible clusters drawn into the visibility buffer: one indirect draw a cluster, its row of the visible list in
// the draw's first instance, each vertex fetched from the pool by the index at its place in the cluster's run of the
// index pool; an imposter cluster's six vertices are its quad's corners instead, and its pixels are kept only where
// its capture has something. The pixel is the row and the triangle, packed, with the hardware depth beside it.
module "WorldRenderer/DrawVisibility";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Scene.Instances;
import Scene.Clusters;
import Scene.Vertices;
import Scene.Visibility;
import Scene.Captures;
import Scene.Imposters;

struct Varyings
{
    float4 position : SV_Position;
    // The visibility id, the same from each of a triangle's three vertices.
    nointerpolation uint id : VISIBILITY_ID;
    // The capture row an imposter reads, 0 for any other cluster.
    nointerpolation uint capture : CAPTURE;
    // Where in the capture's atlas the imposter's pixel is.
    float2 uv : CAPTURE_UV;
};

// Each draw is one instance, so its first instance, the visible row, is the instance drawn: SV_InstanceID would be
// 0, as it counts from the draw's first instance.
[shader("vertex")]
Varyings DrawVisibilityVertex(uint vertexIndex : SV_VertexID, uint instanceIndex : SV_StartInstanceLocation)
{
    FrameConstants frame = GetFrame();
    ArrayRef<VisibleCluster> visible = { frame.visibleClusters };
    VisibleCluster entry = visible[instanceIndex];
    GpuInstance instance = LoadInstance(frame.instances, entry.instance);
    GpuCluster cluster = LoadCluster(frame.clusters, entry.cluster);

    Varyings output;
    output.id = EncodeVisibilityId(instanceIndex, vertexIndex / 3u);
    output.capture = 0u;
    output.uv = float2(0.0);
    float3 relative;
    if (IsImposter(cluster))
    {
        float2 quadUv;
        relative = GetImposterCorner(frame, instance, vertexIndex, quadUv);
        output.capture = instance.capture;
        output.uv = GetCaptureUv(GetImposterDirection(frame, instance), quadUv);
    }
    else
    {
        Vertex vertex = LoadVertex(frame.vertices, LoadIndex(frame.indices, cluster.indexOffset + vertexIndex));
        relative = Relative(frame, ToWorld(instance.world, vertex.position));
    }

    output.position = mul(float4(relative, 1.0), frame.viewProjection);
    return output;
}

[shader("pixel")]
uint DrawVisibilityPixel(Varyings input) : SV_Target0
{
    if (input.capture != 0u)
    {
        FrameConstants frame = GetFrame();
        GpuCapture capture = LoadCapture(frame.captures, input.capture);
        TextureRef albedo = { capture.albedo };
        SamplerRef sampler = { frame.captureSampler };
        if (albedo.SampleLevel(sampler, input.uv, 0.0).a < 0.5)
            discard;
    }

    return input.id;
}
