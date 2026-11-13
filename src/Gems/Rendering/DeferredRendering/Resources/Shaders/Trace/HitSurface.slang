// What a ray finds in the world's acceleration structure: how much light gets through along it, and what it landed on
// as a surface, its triangle found through the instance and mesh tables and its material as the hit materials table
// averages it. An instance's geometries are its model's meshes, its hit group offset where they start in the mesh
// table.
module "Trace/HitSurface";

import Core.Heap;
import Core.Frame;
import Core.Math;
import Core.Pack;
import Scene.Instances;
import Scene.Models;
import Scene.Vertices;

// A material row as a ray sees it, C#'s acceleration structures Renderer.HitMaterialBytes long: its surface averaged
// over its whole texture.
public struct GpuHitMaterial
{
    // The base colour a surface reflects diffusely: none of it where it is metal.
    public float3 diffuse;
    // What of the light along a ray gets through the surface, by colour, in 8 bits each: none for an opaque one.
    public uint transmission;
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

// How many pixels' width a ray from a pixel's surface starts off it, beyond a centimetre: the drawn cut of a model's
// clusters may stand that far from the full detail rays hit.
static const float RayOffsetPixels = 4.0;

// How far along its normal a ray from a pixel's surface at a camera-relative point starts.
public float GetRayOffset(FrameConstants frame, float3 relative)
{
    return 0.01 + RayOffsetPixels * length(relative) / frame.pixelsPerRadian;
}

// How much of the light along a ray from origin up to a distance gets through, by colour: all where nothing is in the
// way, none past an opaque surface, and through each other what its material lets through. The world's instances are
// non-opaque, so the query is handed each triangle it lands on, in any order, and stops at the first that lets nothing
// through.
public float3 GetTransmittance(RaytracingAccelerationStructure world, float3 origin, float3 direction, float distance)
{
    Models models = GetModels();
    RayQuery<RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_SKIP_PROCEDURAL_PRIMITIVES> query;
    RayDesc ray;
    ray.Origin = origin;
    ray.Direction = direction;
    ray.TMin = 0.0;
    ray.TMax = distance;
    query.TraceRayInline(world, RAY_FLAG_NONE, 0xFFu, ray);
    ArrayRef<GpuHitMaterial> materials = { GetTracedWorld().hitMaterials };
    float3 through = float3(1.0);
    while (query.Proceed())
    {
        GpuInstance instance = LoadInstance(models.instances, query.CandidateInstanceID());
        uint mesh = query.CandidateInstanceContributionToHitGroupIndex() + query.CandidateGeometryIndex();
        uint slot = LoadMesh(models.meshes, mesh).materialSlot;
        through *= UnpackUnorm4x8(materials[GetMaterial(instance, slot)].transmission).rgb;
        if (Max3(through) <= 0.0)
            query.CommitNonOpaqueTriangleHit();
    }

    return query.CommittedStatus() == COMMITTED_NOTHING ? through : float3(0.0);
}

// How much of the light along a ray from origin, in the world, up to a distance gets through the world, by colour:
// all where nothing is in the way or no acceleration structure was built, none past what is opaque, and through
// glass what its clarity and colour let through.
public float3 GetTransmittance(float3 origin, float3 direction, float distance)
{
    TracedWorld traced = GetTracedWorld();
    if (traced.acceleration == 0u)
        return float3(1.0);

    return GetTransmittance(GetTracedAcceleration(), origin, direction, distance);
}

// What a ray landed on first: whether it landed on anything, the instance's row, the mesh as its hit group offset plus
// its geometry, the triangle in the mesh and the barycentrics of the hit in it, whether it faces the ray, and how far
// along the ray it is.
public struct RayHit
{
    public bool isHit;
    public uint instance;
    public uint mesh;
    public uint triangle;
    public float2 barycentrics;
    public bool isFrontFace;
    public float distance;
}

// What a ray from origin along direction lands on first within a distance, with flags such as
// RAY_FLAG_CULL_BACK_FACING_TRIANGLES added to the query's.
public RayHit GetClosestHit(
    RaytracingAccelerationStructure world, float3 origin, float3 direction, float distance, uint flags)
{
    RayQuery<RAY_FLAG_FORCE_OPAQUE | RAY_FLAG_SKIP_PROCEDURAL_PRIMITIVES> query;
    RayDesc ray;
    ray.Origin = origin;
    ray.Direction = direction;
    ray.TMin = 0.0;
    ray.TMax = distance;
    query.TraceRayInline(world, flags, 0xFFu, ray);
    query.Proceed();
    RayHit hit;
    hit.isHit = query.CommittedStatus() == COMMITTED_TRIANGLE_HIT;
    hit.instance = query.CommittedInstanceID();
    hit.mesh = query.CommittedInstanceContributionToHitGroupIndex() + query.CommittedGeometryIndex();
    hit.triangle = query.CommittedPrimitiveIndex();
    hit.barycentrics = query.CommittedTriangleBarycentrics();
    hit.isFrontFace = query.CommittedTriangleFrontFace();
    hit.distance = query.CommittedRayT();
    return hit;
}

// The surface at a hit of a ray from origin along direction.
public HitSurface LoadHitSurface(RayHit hit, float3 origin, float3 direction)
{
    Models models = GetModels();
    GpuInstance instance = LoadInstance(models.instances, hit.instance);
    GpuMesh mesh = LoadMesh(models.meshes, hit.mesh);
    uint first = mesh.indexOffset + hit.triangle * 3u;
    float3 n0 = LoadVertex(models.vertices, LoadIndex(models.indices, first)).normal;
    float3 n1 = LoadVertex(models.vertices, LoadIndex(models.indices, first + 1u)).normal;
    float3 n2 = LoadVertex(models.vertices, LoadIndex(models.indices, first + 2u)).normal;
    float3 lambda = float3(1.0 - hit.barycentrics.x - hit.barycentrics.y, hit.barycentrics.x, hit.barycentrics.y);
    float3 normal = ToWorldNormal(instance.world, n0 * lambda.x + n1 * lambda.y + n2 * lambda.z);

    ArrayRef<GpuHitMaterial> materials = { GetTracedWorld().hitMaterials };
    GpuHitMaterial material = materials[GetMaterial(instance, mesh.materialSlot)];
    HitSurface surface;
    surface.position = origin + direction * hit.distance;
    surface.normal = dot(normal, direction) > 0.0 ? -normal : normal;
    surface.diffuse = material.diffuse;
    surface.emissive = material.emissive;
    surface.isBackface = !hit.isFrontFace;
    return surface;
}
