// The global distance field: four clipmaps of voxels around the camera, each level's voxels twice the last's, every
// voxel the signed distance to the nearest surface in metres clamped to a band, made from the models' own fields in
// an atlas. C#'s FieldConstants is the twin of the struct here. A clipmap wraps as it slides with the camera, so a
// voxel keeps its place while it stays in the window.
module "Scene/Field";

import Core.Heap;
import Core.Math;

public static const uint FieldLevels = 4u;
public static const uint FieldVoxels = 128u;
public static const uint FieldBrickVoxels = 8u;
public static const uint FieldBricks = FieldVoxels / FieldBrickVoxels;
public static const uint FieldBricksPerLevel = FieldBricks * FieldBricks * FieldBricks;
public static const uint FieldInstancesPerBrick = 64u;
public static const float FieldBandVoxels = 4.0;
public static const uint FieldAtlasVoxels = 256u;
public static const uint FieldModelVoxels = 32u;
public static const uint FieldBrickDirty = 1u;

public struct FieldConstants
{
    // Each level's window: the voxel, counted in that level's voxels, at its corner, in xyz.
    public int4 origins[FieldLevels];
    // Metres a voxel of each level covers.
    public float4 voxelSizes;
    // Heap ids.
    public uint atlas;
    public uint modelFields;
    public uint bricks;
    public uint lists;
    public uint dirtyBricks;
    public uint counts;
    public uint dirtyArguments;
    public uint sampler;
    public uint clipmap0;
    public uint clipmap1;
    public uint clipmap2;
    public uint clipmap3;
    public uint maxDirtyBricks;
    public uint instanceCount;
    public uint dirtyInstances;
    public uint dirtyInstanceCount;
}

// A model's field in the atlas: C#'s GpuModelField, 32 bytes.
public struct GpuModelField
{
    public float3 origin;
    public float voxel;
    public float band;
    public uint padding0;
    public uint padding1;
    public uint padding2;
}

// A brick's entry in the brick table: the brick, counted in bricks, it holds, and its flags.
public struct FieldBrick
{
    public int3 world;
    public uint flags;
}

public static const uint FieldCountDirtyBricks = 0u;

// The instance list's groups go along x up to this many and then down y.
public static const uint FieldListGroupsPerRow = 65535u;

public uint GetClipmap(FieldConstants field, uint level)
{
    return level == 0u ? field.clipmap0 : level == 1u ? field.clipmap1 : level == 2u ? field.clipmap2 : field.clipmap3;
}

// The voxel of a level a point is in.
public int3 GetFieldVoxel(FieldConstants field, uint level, float3 world)
{
    return int3(floor(world / field.voxelSizes[level]));
}

// Whether a voxel is within the level's window, a voxel in from its edges so a sample's neighbours are too.
public bool IsInFieldWindow(FieldConstants field, uint level, int3 voxel)
{
    int3 origin = field.origins[level].xyz;
    return all(voxel >= origin + 1) && all(voxel < origin + int(FieldVoxels) - 2);
}

// The brick of a level a voxel is in.
public int3 GetFieldBrick(int3 voxel) { return int3(floor(float3(voxel) / float(FieldBrickVoxels))); }

public int3 GetFieldWindowBrick(FieldConstants field, uint level) { return GetFieldBrick(field.origins[level].xyz); }

// The brick's cell in the level's table: the window wraps, so a brick keeps its cell as the window slides.
public uint GetFieldBrickCell(uint level, int3 brick)
{
    int3 cell = brick & int(FieldBricks - 1u);
    return (level * FieldBricksPerLevel) + (uint(cell.z) * FieldBricks + uint(cell.y)) * FieldBricks + uint(cell.x);
}

// The texel of a level's clipmap a voxel is kept in.
public uint3 GetFieldTexel(int3 voxel) { return uint3(voxel & int(FieldVoxels - 1u)); }

// The atlas texel at a model field's corner.
public uint3 GetModelFieldOrigin(uint slot)
{
    uint perRow = FieldAtlasVoxels / FieldModelVoxels;
    return uint3(slot % perRow, (slot / perRow) % perRow, slot / (perRow * perRow)) * FieldModelVoxels;
}

// The signed distance in metres at a point, from the finest level whose window holds it; the band of the coarsest
// level beyond them all.
public float SampleField(FieldConstants field, float3 world, out uint levelUsed)
{
    SamplerRef sampler = { field.sampler };
    for (uint level = 0u; level < FieldLevels; level++)
    {
        float voxelSize = field.voxelSizes[level];
        int3 voxel = GetFieldVoxel(field, level, world);
        if (!IsInFieldWindow(field, level, voxel))
            continue;

        // Texel centres sit at voxel corners' halves: the sampler wraps the window.
        VolumeRef clipmap = { GetClipmap(field, level) };
        float3 uvw = (world / voxelSize) / float(FieldVoxels);
        levelUsed = level;
        return clipmap.SampleLevel(sampler, uvw, 0.0).x * (FieldBandVoxels * voxelSize);
    }

    levelUsed = FieldLevels - 1u;
    return FieldBandVoxels * field.voxelSizes[FieldLevels - 1u];
}

// The field's gradient at a point: the surface normal where the point is on one.
public float3 SampleFieldNormal(FieldConstants field, float3 world)
{
    uint level;
    SampleField(field, world, level);
    float h = field.voxelSizes[level] * 0.5;
    uint unused;
    float3 gradient = float3(
        SampleField(field, world + float3(h, 0.0, 0.0), unused) - SampleField(field, world - float3(h, 0.0, 0.0), unused),
        SampleField(field, world + float3(0.0, h, 0.0), unused) - SampleField(field, world - float3(0.0, h, 0.0), unused),
        SampleField(field, world + float3(0.0, 0.0, h), unused) - SampleField(field, world - float3(0.0, 0.0, h), unused)
    );
    return NormalizeSafe(gradient, float3(0.0, 1.0, 0.0));
}
