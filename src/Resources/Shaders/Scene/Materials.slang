// The material table: a row per material slot drawn, C#'s GpuMaterial, 64 bytes each: the number of the material
// shader that draws it, then that shader's struct, its params, as the renderer packed them from the shader's
// reflection. Row 0 is the default material, which a material whose asset has not arrived is drawn with.
module "Scene/Materials";

import Core.Heap;

public static const uint GpuMaterialBytes = 64u;

// The number of the material shader that draws the row.
public uint LoadMaterialShader(uint table, uint row)
{
    BufferRef materials = { table };
    return materials.Load<uint>(row * GpuMaterialBytes);
}

// The row's params as the struct of the material shader that draws it.
public T LoadMaterialParams<T>(uint table, uint row)
{
    BufferRef materials = { table };
    return materials.Load<T>(row * GpuMaterialBytes + 4u);
}
