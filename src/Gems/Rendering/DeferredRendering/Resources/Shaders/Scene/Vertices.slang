// The vertex pool: every resident model's vertices in one buffer, C#'s GpuVertex, 32 bytes each, which the index pool
// points into. A triangle is three pool indices.
module "Scene/Vertices";

import Core.Heap;
import Core.Pack;

public struct GpuVertex
{
    public float3 position;
    public uint normal;
    public uint tangent;
    public float tangentSign;
    public float2 uv;
}

// A vertex unpacked, in its model's frame.
public struct Vertex
{
    public float3 position;
    public float3 normal;
    public float3 tangent;
    // 1 or -1: what turns the cross of the normal and the tangent into the bitangent.
    public float tangentSign;
    public float2 uv;
}

public Vertex LoadVertex(uint pool, uint index)
{
    ArrayRef<GpuVertex> vertices = { pool };
    GpuVertex packed = vertices[index];
    Vertex vertex;
    vertex.position = packed.position;
    vertex.normal = DecodeUnitVector(packed.normal);
    vertex.tangent = DecodeUnitVector(packed.tangent);
    vertex.tangentSign = packed.tangentSign;
    vertex.uv = packed.uv;
    return vertex;
}

// A pool index of the index pool.
public uint LoadIndex(uint pool, uint index)
{
    ArrayRef<uint> indices = { pool };
    return indices[index];
}
