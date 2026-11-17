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

// What of the light along a ray gets through the triangle of a mesh, its hit group offset plus its geometry, of an
// instance's row, by colour.
float3 GetTransmission(Models models, uint instance, uint mesh)
{
    ArrayRef<GpuHitMaterial> materials = { GetTracedWorld().hitMaterials };
    uint slot = LoadMesh(models.meshes, mesh).materialSlot;
    return UnpackUnorm4x8(materials[GetMaterial(LoadInstance(models.instances, instance), slot)].transmission).rgb;
}

// How much of the light along a ray from origin up to a distance gets through, by colour: all where nothing is in the
// way, none past an opaque surface, and through each other what its material lets through. The world's instances are
// non-opaque, so the query is handed each triangle it lands on, in any order, and stops at the first that lets nothing
// through.
public float3 GetTransmittance(RaytracingAccelerationStructure world, float3 origin, float3 direction, float distance)
{
    return GetTransmittance(world, origin, direction, distance, 0.0);
}

// The same, passing the outer sides of the triangles nearer to the origin than a reach, so a ray that starts close to
// its surface does not land on that surface's own outer side, while a caster's inner side still stops it.
public float3 GetTransmittance(
    RaytracingAccelerationStructure world, float3 origin, float3 direction, float distance, float frontReach)
{
    Models models = GetModels();
    RayQuery<RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_SKIP_PROCEDURAL_PRIMITIVES> query;
    RayDesc ray;
    ray.Origin = origin;
    ray.Direction = direction;
    ray.TMin = 0.0;
    ray.TMax = distance;
    query.TraceRayInline(world, RAY_FLAG_NONE, 0xFFu, ray);
    float3 through = float3(1.0);
    while (query.Proceed())
    {
        if (query.CandidateTriangleFrontFace() && query.CandidateTriangleRayT() < frontReach)
            continue;

        uint mesh = query.CandidateInstanceContributionToHitGroupIndex() + query.CandidateGeometryIndex();
        through *= GetTransmission(models, query.CandidateInstanceID(), mesh);
        if (Max3(through) <= 0.0)
            query.CommitNonOpaqueTriangleHit();
    }

    return query.CommittedStatus() == COMMITTED_NOTHING ? through : float3(0.0);
}

// The same, and in occluder how far along the ray the nearest surface that lets nothing through is, 0 where the light
// gets through: the query goes on past the first such surface to find the nearest, which a shadow's width follows.
public float3 GetTransmittance(
    RaytracingAccelerationStructure world,
    float3 origin,
    float3 direction,
    float distance,
    float frontReach,
    out float occluder)
{
    Models models = GetModels();
    RayQuery<RAY_FLAG_SKIP_PROCEDURAL_PRIMITIVES> query;
    RayDesc ray;
    ray.Origin = origin;
    ray.Direction = direction;
    ray.TMin = 0.0;
    ray.TMax = distance;
    query.TraceRayInline(world, RAY_FLAG_NONE, 0xFFu, ray);
    float3 through = float3(1.0);
    while (query.Proceed())
    {
        if (query.CandidateTriangleFrontFace() && query.CandidateTriangleRayT() < frontReach)
            continue;

        uint mesh = query.CandidateInstanceContributionToHitGroupIndex() + query.CandidateGeometryIndex();
        float3 passed = GetTransmission(models, query.CandidateInstanceID(), mesh);
        if (Max3(passed) <= 0.0)
            query.CommitNonOpaqueTriangleHit();
        else
            through *= passed;
    }

    bool isBlocked = query.CommittedStatus() != COMMITTED_NOTHING;
    occluder = isBlocked ? query.CommittedRayT() : 0.0;
    return isBlocked ? float3(0.0) : through;
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

// What a ray landed on first, passing glass: whether it landed on anything, the instance's row, the mesh as its hit
// group offset plus its geometry, the triangle in the mesh and the barycentrics of the hit in it, whether it faces the
// ray, how far along the ray it is, and how much of the light along the ray gets through the glass before it, or
// before the end of the ray where it landed on nothing.
public struct RayHit
{
    public bool isHit;
    public uint instance;
    public uint mesh;
    public uint triangle;
    public float2 barycentrics;
    public bool isFrontFace;
    public float distance;
    public float3 through;
}

// What a ray from origin along direction lands on first within a distance, with flags such as
// RAY_FLAG_CULL_BACK_FACING_TRIANGLES added to the query's. A surface that lets light through is passed, as a shadow
// ray passes it. The ray first lands on the nearest triangle of any material, which the hardware finds alone: where
// that lets nothing through, it is the hit and no glass is nearer. Else the ray traces again and the query is handed
// each triangle it lands on, in any order, and commits those that let nothing through. Only a ray that passed glass
// nearer than its end traces again, for the light that gets through.
public RayHit GetClosestHit(
    RaytracingAccelerationStructure world, float3 origin, float3 direction, float distance, uint flags)
{
    Models models = GetModels();
    RayQuery<RAY_FLAG_SKIP_PROCEDURAL_PRIMITIVES> query;
    RayDesc ray;
    ray.Origin = origin;
    ray.Direction = direction;
    ray.TMin = 0.0;
    ray.TMax = distance;
    query.TraceRayInline(world, flags | RAY_FLAG_FORCE_OPAQUE, 0xFFu, ray);
    query.Proceed();
    float nearestGlass = distance;
    uint nearest = query.CommittedInstanceContributionToHitGroupIndex() + query.CommittedGeometryIndex();
    if (query.CommittedStatus() == COMMITTED_TRIANGLE_HIT
        && Max3(GetTransmission(models, query.CommittedInstanceID(), nearest)) > 0.0)
    {
        query.TraceRayInline(world, flags, 0xFFu, ray);
        while (query.Proceed())
        {
            uint mesh = query.CandidateInstanceContributionToHitGroupIndex() + query.CandidateGeometryIndex();
            if (Max3(GetTransmission(models, query.CandidateInstanceID(), mesh)) <= 0.0)
                query.CommitNonOpaqueTriangleHit();
            else
                nearestGlass = min(nearestGlass, query.CandidateTriangleRayT());
        }
    }

    RayHit hit;
    hit.isHit = query.CommittedStatus() == COMMITTED_TRIANGLE_HIT;
    hit.instance = query.CommittedInstanceID();
    hit.mesh = query.CommittedInstanceContributionToHitGroupIndex() + query.CommittedGeometryIndex();
    hit.triangle = query.CommittedPrimitiveIndex();
    hit.barycentrics = query.CommittedTriangleBarycentrics();
    hit.isFrontFace = query.CommittedTriangleFrontFace();
    hit.distance = query.CommittedRayT();

    // Short of the surface it landed on, so the second trace does not stop at it.
    float reach = hit.isHit ? hit.distance * 0.999 : distance;
    hit.through = nearestGlass < reach ? GetTransmittance(world, origin, direction, reach) : float3(1.0);
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
