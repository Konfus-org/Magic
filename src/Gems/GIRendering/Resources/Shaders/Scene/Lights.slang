// The light table and the froxel grid the lights are binned into: every local light drawn this frame, C#'s
// GpuLight, 64 bytes each, and for each froxel of the view the lights that reach it, so a pixel loops over its
// froxel's few lights and not the thousand in the world.
module "Scene/Lights";

import Core.Heap;
import Core.Frame;

public static const uint LightPoint = 0u;
public static const uint LightSpot = 1u;
public static const uint LightRect = 2u;

// The froxel grid: tiles across and down the view, and slices in depth, spread so each is as deep as it is wide.
public static const uint3 LightGrid = uint3(32u, 18u, 48u);
public static const uint LightsPerFroxel = 128u;
public static const float LightGridNear = 0.1;
public static const float LightGridFar = 512.0;

public struct GpuLight
{
    // In the world.
    public float3 position;
    public float range;
    // Along the light's +Z, unit.
    public float3 direction;
    // The source's radius for a point light, in metres.
    public float radius;
    // Colour times intensity, at the frame's exposure.
    public float3 radiance;
    public uint type;
    public float cosOuter;
    public float cosInner;
    public float width;
    public float height;
}

public GpuLight LoadLight(uint table, uint index)
{
    ArrayRef<GpuLight> lights = { table };
    return lights[index];
}

// The slice a view depth falls in, from 0 at the near end.
public uint GetLightSlice(float viewDepth)
{
    float t = log(max(viewDepth, LightGridNear) / LightGridNear) / log(LightGridFar / LightGridNear);
    return min(uint(t * float(LightGrid.z)), LightGrid.z - 1u);
}

// The view depth where a slice begins.
public float GetLightSliceDepth(uint slice)
{
    return LightGridNear * pow(LightGridFar / LightGridNear, float(slice) / float(LightGrid.z));
}

public uint GetFroxelIndex(uint3 froxel) { return (froxel.z * LightGrid.y + froxel.y) * LightGrid.x + froxel.x; }

// The froxel a pixel's receiver is in.
public uint3 GetFroxel(FrameConstants frame, uint2 pixel, float viewDepth)
{
    uint2 tile = min((pixel * LightGrid.xy) / uint2(frame.width, frame.height), LightGrid.xy - 1u);
    return uint3(tile, GetLightSlice(viewDepth));
}

// The lights in a froxel: how many, and each one's index in the table.
public uint GetFroxelLightCount(uint lists, uint froxel)
{
    ArrayRef<uint> counts = { lists };
    return min(counts[froxel * (LightsPerFroxel + 1u)], LightsPerFroxel);
}

public uint GetFroxelLight(uint lists, uint froxel, uint i)
{
    ArrayRef<uint> counts = { lists };
    return counts[(froxel * (LightsPerFroxel + 1u)) + 1u + i];
}
