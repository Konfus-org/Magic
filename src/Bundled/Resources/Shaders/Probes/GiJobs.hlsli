// The jobs of making the scene's field again: each an instance of a model with a distance field and a level whose
// made-again bricks its field reaches, as GiCull lists them in GiJobs, the count first. A job is a model's distance field
// placed where its instance stands, read at each voxel it reaches, painted with its first material slot's colours.

#ifndef MAGIC_GI_JOBS_HLSLI
#define MAGIC_GI_JOBS_HLSLI

#include "Lighting/Gi.hlsli"

// Groups that share the jobs out, each taking every this many; texels a side of a model's field; and texels of its
// largest step past a surface its distance is exact to, past which it says only that it is at least that far, as the
// renderer's MeshFields makes it.
static const uint GiJobGroups = 4096u;
static const uint MeshFieldResolution = 32u;
static const float MeshFieldBand = 2.5;

struct GiJob
{
    GpuMeshField field;
    uint material;
    uint level;
    GpuGiLevel info;
    float3 inverse0;
    float3 inverse1;
    float3 inverse2;
    float3 translation;
    float scale;
    float thickness;
    int3 low;
    int3 high;
};

// Where an instance stands in the world, from the origin the scene's field is placed about.
float3 GiTranslation(GpuSceneInstance instance)
{
    return float3(instance.row0.w, instance.row1.w, instance.row2.w);
}

// The box a model's field covers where its instance stands: its middle and half its size along each axis.
void GiWorldBox(GpuSceneInstance instance, GpuMeshField field, out float3 centre, out float3 extent)
{
    float3 half = field.size * 0.5;
    centre = SceneDirection(instance, field.min + half) + GiTranslation(instance);
    extent = float3(dot(abs(instance.row0.xyz), half), dot(abs(instance.row1.xyz), half), dot(abs(instance.row2.xyz), half));
}

// The voxels of a level a box reaches, GiReach past its faces, from the world's origin.
void GiReachOf(GpuGiLevel level, float3 centre, float3 extent, out int3 low, out int3 high)
{
    float reach = GiReach * level.voxel;
    low = (int3)ceil((centre - extent - reach) / level.voxel - 0.5);
    high = (int3)floor((centre + extent + reach) / level.voxel - 0.5);
}

GiJob GiJobOf(uint index)
{
    uint2 entry = LOAD(GiJobs, uint2, 1u + index);
    GpuSceneInstance instance = LOAD(Instances, GpuSceneInstance, entry.x);
    GiJob job;
    job.field = LOAD(MeshFields, GpuMeshField, LOAD(Models, GpuModel, instance.model).field);
    job.material = SceneMaterial(instance, 0u);
    job.level = entry.y;
    job.info = GiLevel(entry.y);

    // The inverse of the instance's turn and scale: the cofactors of its rows over its determinant, as columns.
    float3 row0 = instance.row0.xyz;
    float3 row1 = instance.row1.xyz;
    float3 row2 = instance.row2.xyz;
    float3 cofactor0 = cross(row1, row2);
    float3 cofactor1 = cross(row2, row0);
    float3 cofactor2 = cross(row0, row1);
    float determinant = dot(row0, cofactor0);
    job.inverse0 = cofactor0 / determinant;
    job.inverse1 = cofactor1 / determinant;
    job.inverse2 = cofactor2 / determinant;
    job.translation = GiTranslation(instance);

    // A distance in the model's space is at least this one in the world's, however it is squashed; and how thick the
    // field's box is in the world where it is thinnest.
    float3 stretch = float3(length(float3(row0.x, row1.x, row2.x)), length(float3(row0.y, row1.y, row2.y)), length(float3(row0.z, row1.z, row2.z)));
    job.scale = min(stretch.x, min(stretch.y, stretch.z));
    float3 box = job.field.size * stretch;
    job.thickness = min(box.x, min(box.y, box.z));

    float3 centre;
    float3 extent;
    GiWorldBox(instance, job.field, centre, extent);
    GiReachOf(job.info, centre, extent, job.low, job.high);
    job.low = max(job.low, job.info.dirtyMin);
    job.high = min(job.high, job.info.dirtyMax);
    return job;
}

// How many voxels a job reaches, and the one at a place in that count.
uint GiJobVoxels(GiJob job)
{
    int3 extent = max(job.high - job.low + 1, 0);
    return (uint)(extent.x * extent.y * extent.z);
}

int3 GiJobVoxel(GiJob job, uint at)
{
    uint3 extent = (uint3)(job.high - job.low + 1);
    return job.low + int3(at % extent.x, (at / extent.x) % extent.y, at / (extent.x * extent.y));
}

#if defined(HAS_MeshFieldAtlas)
// The distance in metres from a place to the job's model where its instance stands: its field read there, trilinear,
// and past the field's box the way to the box added.
float GiMeshDistance(GiJob job, float3 position)
{
    float3 world = position - job.translation;
    float3 local = job.inverse0 * world.x + job.inverse1 * world.y + job.inverse2 * world.z;
    float3 place = (local - job.field.min) / job.field.size;
    float3 inside = saturate(place);
    float width;
    float height;
    float depth;
    MeshFieldAtlas.GetDimensions(width, height, depth);
    float3 texel = job.field.corner + 0.5 + inside * (float)(MeshFieldResolution - 1u);
    float distance = MeshFieldAtlas.SampleLevel(MeshFieldAtlasSampler, texel / float3(width, height, depth), 0.0);
    distance += length((place - inside) * job.field.size);
    return distance * job.scale;
}

// The distance a voxel takes from a job's model: three quarters of a voxel less than the true one, so a surface thinner
// than a voxel, a floor or a wall, still has voxels either side of it below zero, and no ray steps through it; every
// shape is that much fatter for it, which light from everywhere does not show. Never more than half as far as the
// model's own field is exact past its surface, though, where its box is thicker than that fattening: a ball a few voxels
// big would otherwise fill its whole box, as its field says no more than that far anywhere; a floor's or a wall's box,
// thinner, is filled by the fattening anyway. Within the field's reach either way.
float GiJobDistance(GiJob job, int3 voxel)
{
    float reach = GiReach * job.info.voxel;
    float exact = MeshFieldBand * max(job.field.size.x, max(job.field.size.y, job.field.size.z)) / (float)(MeshFieldResolution - 1u) * job.scale;
    float fatter = 0.75 * job.info.voxel;
    fatter = job.thickness <= 2.0 * fatter ? fatter : min(fatter, 0.5 * exact);
    return clamp(GiMeshDistance(job, (float3(voxel) + 0.5) * job.info.voxel) - fatter, -reach, reach);
}
#endif

#endif
