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
public static const uint CountWantedRows = 52u;

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

// The barycentrics of the point that a ray from the camera hits in the plane of the camera-relative triangle p0 to
// p2, which are perspective-correct as they are the point's own. Each row of weigh, dotted with the ray, is in
// proportion to a vertex's weight in the hit point, and the last row to their sum. Any length of the ray gives the same
// weights, and no vertex's clip w is divided by, so a vertex near the camera's plane does not lose precision.
float3 Weigh(float3x3 weigh, float3 ray)
{
    float3 weights = mul(weigh, ray);
    float2 yz = weights.xy / weights.z;
    return float3(1.0 - yz.x - yz.y, yz);
}

// The barycentrics of the camera-relative triangle p0 to p2 along ray, a ray from the camera through a pixel, and
// how they change to the rays through the pixel one across and one down.
public Barycentrics GetBarycentrics(float3 p0, float3 p1, float3 p2, float3 ray, float3 across, float3 down)
{
    float3 e1 = p1 - p0;
    float3 e2 = p2 - p0;
    float3x3 weigh = float3x3(cross(e2, p0), cross(p0, e1), cross(e1, e2));
    Barycentrics result;
    result.lambda = Weigh(weigh, ray);
    result.ddx = Weigh(weigh, across) - result.lambda;
    result.ddy = Weigh(weigh, down) - result.lambda;
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

// The visible triangle's vertices and the barycentrics of the pixel whose centre is at the texture coordinate uv.
public PixelTriangle LoadPixelTriangle(FrameConstants frame, VisibleTriangle visible, float2 uv)
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
    float2 texel = 1.0 / float2(frame.width, frame.height);
    result.bary = GetBarycentrics(
        result.p0,
        result.p1,
        result.p2,
        ReconstructRelative(frame, uv, 1.0),
        ReconstructRelative(frame, uv + float2(texel.x, 0.0), 1.0),
        ReconstructRelative(frame, uv + float2(0.0, texel.y), 1.0)
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
