// What a ray finds in the world's acceleration structure: whether anything lies along it, and what it landed on as a
// surface, its triangle found through the instance and leaf cluster tables and its material as the hit materials table
// averages it. An instance's geometries are its model's leaf clusters, its hit group offset where they start in the leaf
// table.
module "Trace/HitSurface";

import Core.Heap;
import Core.Frame;
import Core.Math;
import Scene.Instances;
import Scene.Clusters;
import Scene.Vertices;

// A material row as a ray sees it, C#'s RayTracing.HitMaterialBytes long: its surface averaged over its whole texture.
public struct GpuHitMaterial
{
    // The base colour a surface reflects diffusely: none of it where it is metal.
    public float3 diffuse;
    public uint padding0;
    public float3 emissive;
    public uint padding1;
}

public struct HitSurface
{
    // In the world.
    public float3 position;
    // Unit, in the world, facing the ray.
    public float3 normal;
    public float3 diffuse;
    public float3 emissive;
    // Whether the ray reached the triangle's back, as from inside what it closes.
    public bool isBackface;
}

// Whether nothing is in the way along a ray from origin up to a distance.
public bool CanReach(RaytracingAccelerationStructure world, float3 origin, float3 direction, float distance)
{
    RayQuery<RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_FORCE_OPAQUE | RAY_FLAG_SKIP_PROCEDURAL_PRIMITIVES> query;
    RayDesc ray;
    ray.Origin = origin;
    ray.Direction = direction;
    ray.TMin = 0.0;
    ray.TMax = distance;
    query.TraceRayInline(world, RAY_FLAG_NONE, 0xFFu, ray);
    query.Proceed();
    return query.CommittedStatus() == COMMITTED_NOTHING;
}

// What a query committed to: the instance's row, the leaf as its hit group offset plus its geometry, the triangle in
// the leaf and the barycentrics of the hit in it, whether it faces the ray, and how far along the ray it is.
public struct RayHit
{
    public uint instance;
    public uint leaf;
    public uint triangle;
    public float2 barycentrics;
    public bool isFrontFace;
    public float distance;
}

// The surface at a hit of a ray from origin along direction.
public HitSurface LoadHitSurface(FrameConstants frame, RayHit hit, float3 origin, float3 direction)
{
    ArrayRef<uint> leaves = { frame.leafClusters };
    GpuInstance instance = LoadInstance(frame.instances, hit.instance);
    GpuCluster cluster = LoadCluster(frame.clusters, leaves[hit.leaf]);
    uint first = cluster.indexOffset + hit.triangle * 3u;
    float3 n0 = LoadVertex(frame.vertices, LoadIndex(frame.indices, first)).normal;
    float3 n1 = LoadVertex(frame.vertices, LoadIndex(frame.indices, first + 1u)).normal;
    float3 n2 = LoadVertex(frame.vertices, LoadIndex(frame.indices, first + 2u)).normal;
    float3 lambda = float3(1.0 - hit.barycentrics.x - hit.barycentrics.y, hit.barycentrics.x, hit.barycentrics.y);
    float3 normal = ToWorldNormal(instance.world, n0 * lambda.x + n1 * lambda.y + n2 * lambda.z);

    ArrayRef<GpuHitMaterial> materials = { frame.hitMaterials };
    GpuHitMaterial material = materials[GetMaterial(instance, cluster.materialSlot)];
    HitSurface surface;
    surface.position = origin + direction * hit.distance;
    surface.normal = dot(normal, direction) > 0.0 ? -normal : normal;
    surface.diffuse = material.diffuse;
    surface.emissive = material.emissive;
    surface.isBackface = !hit.isFrontFace;
    return surface;
}
