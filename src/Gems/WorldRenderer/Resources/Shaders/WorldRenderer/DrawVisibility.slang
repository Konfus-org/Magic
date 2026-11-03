// The visible clusters drawn into the visibility buffer: one indirect draw a cluster, its row of the visible list in
// the draw's first instance, each vertex fetched from the pool by the index at its place in the cluster's run of the
// index pool. The pixel is the row and the triangle, packed, with the hardware depth beside it.
module "WorldRenderer/DrawVisibility";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Scene.Instances;
import Scene.Clusters;
import Scene.Vertices;
import Scene.Visibility;

struct Varyings
{
    float4 position : SV_Position;
    // The visibility id, the same from each of a triangle's three vertices.
    nointerpolation uint id : VISIBILITY_ID;
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
    Vertex vertex = LoadVertex(frame.vertices, LoadIndex(frame.indices, cluster.indexOffset + vertexIndex));
    float3 world = ToWorld(instance.world, vertex.position);

    Varyings output;
    output.position = mul(float4(world, 1.0), frame.viewProjection);
    output.id = EncodeVisibilityId(instanceIndex, vertexIndex / 3u);
    return output;
}

[shader("pixel")]
uint DrawVisibilityPixel(Varyings input) : SV_Target0
{
    return input.id;
}
