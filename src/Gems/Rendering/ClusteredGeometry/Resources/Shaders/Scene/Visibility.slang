// What the cluster cull leaves for the draw and the resolve: the visible-cluster list, the counts buffer's word the
// cluster budget reads and the ids the visibility buffer holds (Burns and Hunt, "The Visibility Buffer", 2013), and the
// barycentrics of a pixel in its triangle with their screen derivatives (Schied and Dachsbacher, "Deferred Attribute
// Interpolation for Memory-Efficient Deferred Shading", 2015), and the triangle a pixel shows, which the passes after
// the draw read their surface from.
module "Scene/Visibility";

import Core.Heap;
import Core.Frame;
import Scene.Instances;
import Scene.Clusters;
import Scene.Vertices;
import Surface.Surface;
import Scene.Models;

// One cluster drawn this frame: its row of the visible list is what the visibility buffer's ids name.
public struct VisibleCluster
{
    public uint instance;
    public uint cluster;
}

// The counts buffer's word, as a byte offset, that holds the rows of the visible list the frame's instances asked
// for, which the cluster budget reads next frame.
public static const uint CountWantedRows = 36u;

// A visibility id: the visible list's row and the triangle in it. 0 is no surface.
public uint EncodeVisibilityId(uint visibleIndex, uint triangle)
{
    return ((visibleIndex + 1u) * MaxClusterTriangles) | (triangle % MaxClusterTriangles);
}

// The instance, cluster and triangle a visibility id other than 0 names.
public struct VisibleTriangle
{
    public GpuInstance instance;
    public GpuCluster cluster;
    public uint triangle;
}

public VisibleTriangle LoadVisibleTriangle(uint id)
{
    ClusterConstants clusters = GetClusters();
    ArrayRef<VisibleCluster> visible = { clusters.visibleClusters };
    VisibleCluster entry = visible[id / MaxClusterTriangles - 1u];
    VisibleTriangle result;
    result.instance = LoadInstance(GetModels().instances, entry.instance);
    result.cluster = LoadCluster(clusters.clusters, entry.cluster);
    result.triangle = id % MaxClusterTriangles;
    return result;
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
public Barycentrics GetBarycentrics(float4 clip0, float4 clip1, float4 clip2, float2 pixelNdc, float2 size)
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

// The visible triangle's vertices and the barycentrics of the pixel at pixelNdc, clip-space xy in [-1, 1] with y up.
public PixelTriangle LoadPixelTriangle(FrameConstants frame, VisibleTriangle visible, float2 pixelNdc)
{
    Models models = GetModels();
    ClusterConstants clusters = GetClusters();
    PixelTriangle result;
    result.instance = visible.instance;
    result.cluster = visible.cluster;

    uint first = result.cluster.indexOffset + visible.triangle * 3u;
    uint vertices = GetVertexPool(result.cluster, clusters, models.vertices);
    result.v0 = LoadVertex(vertices, LoadIndex(clusters.indices, first));
    result.v1 = LoadVertex(vertices, LoadIndex(clusters.indices, first + 1u));
    result.v2 = LoadVertex(vertices, LoadIndex(clusters.indices, first + 2u));
    result.p0 = ToCameraRelative(frame, ToWorld(result.instance.world, result.v0.position));
    result.p1 = ToCameraRelative(frame, ToWorld(result.instance.world, result.v1.position));
    result.p2 = ToCameraRelative(frame, ToWorld(result.instance.world, result.v2.position));
    result.bary = GetBarycentrics(
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
    return inputs;
}
