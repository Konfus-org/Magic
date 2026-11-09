// The frame's models: the instances drawn and the pools and tables they are drawn from, C#'s GpuModels, in the frame's
// buffer right after the frame. On a GPU that traces rays, each model's acceleration structure has a geometry for
// each of its meshes, whose row of the mesh table says where its triangles are in the index pool and which material
// slot they take.
module "Scene/Models";

import Core.Heap;
import Core.Pass;
import Core.Frame;

// Where the models are in the frame's buffer, and their bytes: C#'s GpuModels.
public static const uint ModelsOffset = FrameBytes;
public static const uint ModelsBytes = 56u;

public struct Models
{
    // The instance table and how many instances it holds this frame.
    public uint instances;
    public uint count;
    // The vertex and index pools and the material table.
    public uint vertices;
    public uint indices;
    public uint materials;
    // On a GPU that traces rays: the top-level acceleration structure over the instances, 0 when none, each material
    // row as a ray hit reads it, and the mesh table its instances' geometries name.
    public uint acceleration;
    public uint hitMaterials;
    public uint meshes;
    // The box over the world boxes of the instances rays see; min over max where none do.
    public float3 boxMin;
    public float3 boxMax;
}

// A mesh as a ray sees it, C#'s GpuMesh: where its triangles start in the index pool and its material slot.
public struct GpuMesh
{
    public uint indexOffset;
    public uint materialSlot;
}

public Models GetModels()
{
    BufferRef frame = { GetPassHeader().x };
    return frame.Load<Models>(ModelsOffset);
}

public GpuMesh LoadMesh(uint table, uint index)
{
    ArrayRef<GpuMesh> meshes = { table };
    return meshes[index];
}
