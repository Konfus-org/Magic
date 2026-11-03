// The material table: a row per material drawn, C#'s GpuMaterial, 48 bytes each; row 0 is the default, which a
// material whose asset has not arrived is drawn with.
module "Scene/Materials";

import Core.Heap;

// Which material contract a row is drawn with.
public static const uint MaterialKindStandard = 0u;
public static const uint MaterialKindFailure = 1u;

public struct GpuMaterial
{
    public float4 baseColor;
    public float3 emissive;
    public float roughness;
    public float metalness;
    // The heap ids of the base colour texture and the sampler it is read with; 0 for none.
    public uint baseColorTexture;
    public uint baseColorSampler;
    public uint kind;
}

public GpuMaterial LoadMaterial(uint table, uint row)
{
    ArrayRef<GpuMaterial> materials = { table };
    return materials[row];
}
