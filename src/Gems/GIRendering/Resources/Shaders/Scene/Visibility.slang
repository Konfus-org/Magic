// What the cluster cull leaves for the draw and the resolve: the visible-cluster list, the draw arguments, the counts
// buffer's words and the ids the visibility buffer holds (Burns and Hunt, "The Visibility Buffer", 2013), and the
// barycentrics of a pixel in its triangle with their screen derivatives (Schied and Dachsbacher, "Deferred Attribute
// Interpolation for Memory-Efficient Deferred Shading", 2015), and the triangle a pixel shows, which the passes after
// the draw read their surface from.
module "Scene/Visibility";

import Core.Heap;
import Core.Frame;
import Core.Math;
import Scene.Instances;
import Scene.Clusters;
import Scene.Vertices;
import Surface.Surface;

// One cluster drawn this frame: its row of the visible list is what the visibility buffer's ids name.
public struct VisibleCluster
{
    public uint instance;
    public uint cluster;
}

// One draw's arguments, as the GPU reads them: a cluster's triangles three vertices each, its visible row the first
// instance. The draw is not indexed, so a vertex's id is its place in the cluster and names its triangle, which a
// pixel shader cannot ask for on every GPU.
public struct DrawArguments
{
    public uint vertexCount;
    public uint instanceCount;
    public uint firstVertex;
    public uint firstInstance;
}

public static const uint DrawArgumentsBytes = 16u;

// The counts buffer's words, as byte offsets: what the cull passes add to and the draws read their counts from.
public static const uint CountVisibleDraws = 0u;
public static const uint CountRetest = 4u;
public static const uint CountRetestDraws = 8u;
public static const uint CountVisibleList = 12u;
// The translucent clusters' draws, from both cull passes, in the draws' fifth region.
public static const uint CountGlassDraws = 40u;
public static const uint GlassRegion = 4u;

// The rows of the visible list the frame's instances asked for, which the cluster budget reads next frame, and the
// first distance bin with no room left: instances in it and past it draw as their impostors.
public static const uint CountWantedRows = 32u;
public static const uint CountFitBins = 36u;

// The counts buffer's distance bins from DemandBinsOffset, a word each: the rows asked for by the instances whose
// nearest point is that far from the camera, four bins to each doubling of the distance in metres.
public static const uint DemandBinsOffset = 64u;
public static const uint DemandBins = 64u;

public uint GetDemandBin(float distance) { return min(uint(log2(max(distance, 1.0)) * 4.0), DemandBins - 1u); }

// The retest arguments buffer: the groups of the retest dispatch, then 1 and 1.
public static const uint RetestGroups = 0u;
public static const uint RetestThreads = 64u;

// The instance cull's groups go along x up to this many and then down y, as a GPU takes no more along one axis.
public static const uint CullGroupsPerRow = 65535u;

// A visibility id: the visible list's row and the triangle in it; 0 is no surface.
public uint EncodeVisibilityId(uint visibleIndex, uint triangle) { return ((visibleIndex + 1u) << 7u) | (triangle & 127u); }

public bool DecodeVisibilityId(uint id, out uint visibleIndex, out uint triangle)
{
    triangle = id & 127u;
    visibleIndex = (id >> 7u) - 1u;
    return id != 0u;
}

// A pixel's barycentrics in its triangle, perspective-correct, and how they change over a pixel across and down.
public struct Barycentrics
{
    public float3 lambda;
    public float3 ddx;
    public float3 ddy;
}

// The barycentrics at pixelNdc, clip-space xy in [-1, 1] with y up, of the triangle whose vertices are clip0 to
// clip2 after projection, for an output size pixels across and down.
public Barycentrics ComputeBarycentrics(float4 clip0, float4 clip1, float4 clip2, float2 pixelNdc, float2 size)
{
    float3 invW = 1.0 / float3(clip0.w, clip1.w, clip2.w);
    float2 ndc0 = clip0.xy * invW.x;
    float2 ndc1 = clip1.xy * invW.y;
    float2 ndc2 = clip2.xy * invW.z;
    float invDet = 1.0 / determinant(float2x2(ndc2 - ndc1, ndc0 - ndc1));
    float3 ddx = float3(ndc1.y - ndc2.y, ndc2.y - ndc0.y, ndc0.y - ndc1.y) * invDet * invW;
    float3 ddy = float3(ndc2.x - ndc1.x, ndc0.x - ndc2.x, ndc1.x - ndc0.x) * invDet * invW;
    float ddxSum = ddx.x + ddx.y + ddx.z;
    float ddySum = ddy.x + ddy.y + ddy.z;
    float2 delta = pixelNdc - ndc0;
    float interpInvW = invW.x + delta.x * ddxSum + delta.y * ddySum;
    float interpW = 1.0 / interpInvW;

    Barycentrics result;
    result.lambda.x = interpW * (invW.x + delta.x * ddx.x + delta.y * ddy.x);
    result.lambda.y = interpW * (delta.x * ddx.y + delta.y * ddy.y);
    result.lambda.z = interpW * (delta.x * ddx.z + delta.y * ddy.z);

    // A pixel across is 2 / width of clip space, and a pixel down is -2 / height, as y points up.
    ddx *= 2.0 / size.x;
    ddy *= -2.0 / size.y;
    ddxSum *= 2.0 / size.x;
    ddySum *= -2.0 / size.y;
    float interpWddx = 1.0 / (interpInvW + ddxSum);
    float interpWddy = 1.0 / (interpInvW + ddySum);
    result.ddx = interpWddx * (result.lambda * interpInvW + ddx) - result.lambda;
    result.ddy = interpWddy * (result.lambda * interpInvW + ddy) - result.lambda;
    return result;
}

public float3 Interpolate(float3 a, float3 b, float3 c, float3 lambda) { return a * lambda.x + b * lambda.y + c * lambda.z; }

public float2 Interpolate(float2 a, float2 b, float2 c, float3 lambda) { return a * lambda.x + b * lambda.y + c * lambda.z; }

// The triangle a pixel shows: the instance and cluster it is from, its vertices in the model's frame, where they are
// relative to the camera, and the pixel's barycentrics in it.
public struct PixelTriangle
{
    public GpuInstance instance;
    public GpuCluster cluster;
    public Vertex v0;
    public Vertex v1;
    public Vertex v2;
    public float3 p0;
    public float3 p1;
    public float3 p2;
    public Barycentrics bary;
}

// The triangle a decoded visibility id names, with the barycentrics of the pixel at pixelNdc, clip-space xy in
// [-1, 1] with y up.
public PixelTriangle LoadPixelTriangle(FrameConstants frame, uint visibleIndex, uint triangle, float2 pixelNdc)
{
    ClusterConstants clusters = GetClusters();
    ArrayRef<VisibleCluster> visible = { clusters.visibleClusters };
    VisibleCluster entry = visible[visibleIndex];
    PixelTriangle result;
    result.instance = LoadInstance(frame.instances, entry.instance);
    result.cluster = LoadCluster(clusters.clusters, entry.cluster);

    uint first = result.cluster.indexOffset + triangle * 3u;
    result.v0 = LoadVertex(frame.vertices, LoadIndex(frame.indices, first));
    result.v1 = LoadVertex(frame.vertices, LoadIndex(frame.indices, first + 1u));
    result.v2 = LoadVertex(frame.vertices, LoadIndex(frame.indices, first + 2u));
    result.p0 = Relative(frame, ToWorld(result.instance.world, result.v0.position));
    result.p1 = Relative(frame, ToWorld(result.instance.world, result.v1.position));
    result.p2 = Relative(frame, ToWorld(result.instance.world, result.v2.position));
    result.bary = ComputeBarycentrics(
        mul(float4(result.p0, 1.0), frame.viewProjection),
        mul(float4(result.p1, 1.0), frame.viewProjection),
        mul(float4(result.p2, 1.0), frame.viewProjection),
        pixelNdc,
        float2(frame.width, frame.height)
    );
    return result;
}

// What a material is handed at the pixel of a triangle: its attributes interpolated from the triangle's vertices, in
// the camera-relative world, and how its texture coordinates change over the pixel.
public SurfaceInputs CreateSurfaceInputs(PixelTriangle shown)
{
    float3 lambda = shown.bary.lambda;
    SurfaceInputs inputs;
    inputs.position = Interpolate(shown.p0, shown.p1, shown.p2, lambda);
    float3 normal = Interpolate(shown.v0.normal, shown.v1.normal, shown.v2.normal, lambda);
    float3 tangent = Interpolate(shown.v0.tangent, shown.v1.tangent, shown.v2.tangent, lambda);
    inputs.normal = ToWorldNormal(shown.instance.world, normal);
    inputs.tangent = ToWorldDirection(shown.instance.world, tangent);
    inputs.tangentSign = shown.v0.tangentSign;
    inputs.uv = Interpolate(shown.v0.uv, shown.v1.uv, shown.v2.uv, lambda);
    inputs.uvDdx = Interpolate(shown.v0.uv, shown.v1.uv, shown.v2.uv, shown.bary.ddx);
    inputs.uvDdy = Interpolate(shown.v0.uv, shown.v1.uv, shown.v2.uv, shown.bary.ddy);
    inputs.viewDirection = NormalizeSafe(-inputs.position, float3(0.0, 0.0, -1.0));
    return inputs;
}
