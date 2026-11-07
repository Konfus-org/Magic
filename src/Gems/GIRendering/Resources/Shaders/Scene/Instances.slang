// The instance table: everything drawn this frame, C#'s GpuInstance, 256 bytes each, in the world; a position goes
// camera-relative once transformed (Core/Frame's Relative).
module "Scene/Instances";

import Core.Heap;

public static const uint MaterialSlots = 8u;

public struct GpuInstance
{
    // The model's frame to the world, row-major, for row vectors, the world to the model's frame, and the model's
    // frame to the world before it last changed, in the frame movedFrame.
    public float4x4 world;
    public float4x4 inverseWorld;
    public float4x4 previousWorld;
    // The sphere around the instance in the world.
    public float3 boundsCenter;
    public float boundsRadius;
    // The instance's clusters in the cluster pool.
    public uint clusterOffset;
    public uint clusterCount;
    public uint movedFrame;
    public uint impostor;
    // The material table's row for each slot.
    public uint materials[MaterialSlots];
}

public GpuInstance LoadInstance(uint table, uint index)
{
    ArrayRef<GpuInstance> instances = { table };
    return instances[index];
}

public uint GetMaterial(GpuInstance instance, uint slot) { return instance.materials[min(slot, MaterialSlots - 1u)]; }

// The model's frame to the world last frame: where it was before it moved, when it moved this frame, else where it is.
public float4x4 GetPreviousWorld(GpuInstance instance, uint frameIndex)
{
    return instance.movedFrame == frameIndex ? instance.previousWorld : instance.world;
}

// The most a world matrix scales a length by: the longest of its axes.
public float GetMaxScale(float4x4 world)
{
    float3 x = float3(world[0][0], world[0][1], world[0][2]);
    float3 y = float3(world[1][0], world[1][1], world[1][2]);
    float3 z = float3(world[2][0], world[2][1], world[2][2]);
    return sqrt(max(dot(x, x), max(dot(y, y), dot(z, z))));
}

// A point of the model in the world.
public float3 ToWorld(float4x4 world, float3 point) { return mul(float4(point, 1.0), world).xyz; }

// A direction of the model in the world, unit: what a tangent or a view direction turns by.
public float3 ToWorldDirection(float4x4 world, float3 direction)
{
    return normalize(mul(direction, float3x3(world[0].xyz, world[1].xyz, world[2].xyz)));
}

// A normal of the model in the world, unit: turned by the cofactors of the world's axes, so it stays perpendicular
// to the surface however unevenly the instance is scaled.
public float3 ToWorldNormal(float4x4 world, float3 normal)
{
    float3 x = world[0].xyz;
    float3 y = world[1].xyz;
    float3 z = world[2].xyz;
    return normalize(normal.x * cross(y, z) + normal.y * cross(z, x) + normal.z * cross(x, y));
}
