// The clusters drawn into the sun's pages: one draw a cluster and page, in one pass over the whole pool with no
// attachment. The vertex entry puts the triangle where its page is in the pool and clips it to the page's edges; the
// pixel entry keeps the least light-space depth of each texel by atomics. The pass header's x is the frame's
// constants and y the shadow constants.
module "DrawShadow";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Scene.Instances;
import Scene.Clusters;
import Scene.Vertices;
import Shadow.Virtual;

struct Varyings
{
    float4 position : SV_Position;
    // Signed distances to the page's left, right, bottom and top edges, in metres on the light's plane.
    float clip[4] : SV_ClipDistance;
    // Depth along the light.
    float depth : LIGHT_DEPTH;
};

[shader("vertex")]
Varyings DrawShadowVertex(uint vertexIndex : SV_VertexID, uint drawIndex : SV_StartInstanceLocation)
{
    FrameConstants frame = GetFrame();
    ShadowConstants shadow = LoadShadowConstants(GetPassHeader().y);
    ArrayRef<ShadowDraw> draws = { shadow.draws };
    ShadowDraw draw = draws[drawIndex];
    GpuInstance instance = LoadInstance(frame.instances, draw.instance);
    GpuCluster cluster = LoadCluster(frame.clusters, draw.cluster);
    Vertex vertex = LoadVertex(frame.vertices, LoadIndex(frame.indices, cluster.indexOffset + vertexIndex));
    float3 light = ToLight(shadow, ToWorld(instance.world, vertex.position));

    uint level = draw.page >> 16u;
    ArrayRef<uint2> table = { shadow.pageTable };
    uint2 entry = table[(level * shadow.tableWidth * shadow.tableWidth) + (draw.page & 0xFFFFu)];
    int2 page = UnpackPage(shadow, level, entry.y);
    float pageWorld = GetPageWorld(shadow, level);
    float2 inPage = light.xy - float2(page) * pageWorld;
    float2 uv = inPage / pageWorld;

    // The page's texels in the pool, then the pool as clip space, y up.
    float poolTexels = float(shadow.poolPagesPerRow * shadow.pageTexels);
    float2 texel = float2(GetPoolOrigin(shadow, draw.physical)) + float2(uv.x, 1.0 - uv.y) * float(shadow.pageTexels);
    float2 ndc = float2((texel.x / poolTexels) * 2.0 - 1.0, 1.0 - (texel.y / poolTexels) * 2.0);

    Varyings output;
    output.position = float4(ndc, 0.5, 1.0);
    output.clip[0] = inPage.x;
    output.clip[1] = pageWorld - inPage.x;
    output.clip[2] = inPage.y;
    output.clip[3] = pageWorld - inPage.y;
    output.depth = light.z;
    return output;
}

[shader("pixel")]
void DrawShadowPixel(Varyings input)
{
    ShadowConstants shadow = LoadShadowConstants(GetPassHeader().y);
    UintStorageRef pool = { shadow.pool, 0u };
    pool.InterlockedMin(uint2(input.position.xy), ToDepthKey(input.depth));
}
