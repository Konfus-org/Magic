// The frame's models: the instances drawn and the pools and tables they are drawn from, C#'s GpuModels, in the frame's
// buffer right after the frame; then the world as rays see it, C#'s GpuTracedWorld. On a GPU that traces rays, each
// model's acceleration structure has a geometry for each of its meshes, whose row of the mesh table says where its
// triangles are in the index pool and which material slot they take.
module "Scene/Models";

import Core.Heap;
import Core.Pass;
import Core.Frame;

// Where the models are in the frame's buffer, and their bytes: C#'s GpuModels.
public static const uint ModelsOffset = FrameBytes;
public static const uint ModelsBytes = 24u;

public struct Models
{
    // The instance table and how many instances it holds this frame.
    public uint instances;
    public uint count;
    // The vertex and index pools and the material table.
    public uint vertices;
    public uint indices;
    public uint materials;
    // The mesh table the instances' geometries name.
    public uint meshes;
}

// Where the world as rays see it is in the frame's buffer, right after the models, and its bytes: C#'s GpuTracedWorld.
public static const uint TracedWorldOffset = ModelsOffset + ModelsBytes;
public static const uint TracedWorldBytes = 8u;

// The world as rays see it, all 0 where nothing builds it: the top-level acceleration structure over the instances,
// 0 when none, and each material row as a ray hit reads it.
public struct TracedWorld
{
    public uint acceleration;
    public uint hitMaterials;
}

// A mesh as a ray sees it, C#'s GpuMesh: where its triangles start in the index pool and its material slot.
public struct GpuMesh
{
    public uint indexOffset;
    public uint materialSlot;
}

public Models GetModels() { return LoadFrameRow<Models>(ModelsOffset); }

public TracedWorld GetTracedWorld() { return LoadFrameRow<TracedWorld>(TracedWorldOffset); }

// The world's top-level acceleration structure; only where GetTracedWorld's acceleration isn't 0.
public RaytracingAccelerationStructure GetTracedAcceleration()
{
    AccelerationRef acceleration = { GetTracedWorld().acceleration };
    return acceleration.Get();
}

public GpuMesh LoadMesh(uint table, uint index)
{
    ArrayRef<GpuMesh> meshes = { table };
    return meshes[index];
}
