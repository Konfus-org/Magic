// The visible clusters drawn into the visibility buffer: one indirect draw a cluster, its row of the visible list in
// the draw's first instance, each vertex fetched from the pool by the index at its place in the cluster's run of the
// index pool. The pixel is the row and the triangle, packed, with the hardware depth beside it. The translucent ones
// draw the same way into the glass's buffer and depth, behind none of the opaque.
module "DrawVisibility";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Scene.Instances;
import Scene.Clusters;
import Scene.Vertices;
import Scene.Visibility;
import Scene.Impostors;
import Scene.Models;

struct Varyings
{
    float4 position : SV_Position;
    // The visibility id, the same from each of a triangle's three vertices.
    nointerpolation uint id : VISIBILITY_ID;
    // How far into the impostor handoff the instance is.
    nointerpolation float fade : FADE;
};

// Each draw is one instance, so its first instance, the visible row, is the instance drawn: SV_InstanceID would be
// 0, as it counts from the draw's first instance.
[shader("vertex")]
Varyings DrawVisibilityVertex(uint vertexIndex : SV_VertexID, uint instanceIndex : SV_StartInstanceLocation)
{
    Models models = GetModels();
    ClusterConstants clusters = GetClusters();
    FrameConstants frame = GetFrame();
    ArrayRef<VisibleCluster> visible = { clusters.visibleClusters };
    VisibleCluster entry = visible[instanceIndex];
    GpuInstance instance = LoadInstance(models.instances, entry.instance);
    GpuCluster cluster = LoadCluster(clusters.clusters, entry.cluster);

    Varyings output;
    output.id = EncodeVisibilityId(instanceIndex, vertexIndex / 3u);
    Vertex vertex = LoadVertex(models.vertices, LoadIndex(models.indices, cluster.indexOffset + vertexIndex));
    float3 relative = Relative(frame, ToWorld(instance.world, vertex.position));
    output.position = mul(float4(relative, 1.0), frame.viewProjection);
    output.fade = GetImpostorFade(frame, instance);
    return output;
}

[shader("pixel")]
uint DrawVisibilityPixel(Varyings input) : SV_Target0 { return input.id; }

// A translucent cluster's pixel, kept where it is nearer than the opaque surface there, so the glass's own depth keeps
// the nearest pane.
[shader("pixel")]
uint DrawGlassPixel(Varyings input) : SV_Target0
{
    TextureRef opaque = { GetFrame().depth };
    if (input.position.z < opaque.Load(uint2(input.position.xy), 0u).x)
        discard;

    return input.id;
}

// A cluster of an instance in the impostor handoff, its own pipeline so the plain draw above keeps its early
// depth test: the dither keeps the clusters' share of the pixels, and the impostor draws the rest.
[shader("pixel")]
uint DrawVisibilityFadingPixel(Varyings input) : SV_Target0
{
    if (GetDither(input.position.xy) < input.fade)
        discard;

    return input.id;
}
