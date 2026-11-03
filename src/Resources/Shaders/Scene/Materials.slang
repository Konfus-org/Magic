// The material table: a row per material drawn, C#'s GpuMaterial, 48 bytes each; row 0 is the default, which a
// material whose asset has not arrived is drawn with.
module "Scene/Materials";

import Core.Heap;

public struct GpuMaterial
{
    public float4 baseColor;
    public float3 emissive;
    public float roughness;
    public float metalness;
    // The heap id of the base colour texture; 0 for none.
    public uint baseColorTexture;
    public uint flags;
    public uint padding;
}

public GpuMaterial LoadMaterial(uint table, uint row)
{
    ArrayRef<GpuMaterial> materials = { table };
    return materials[row];
}
