// The material table: a row per material slot drawn, C#'s GpuMaterial, 64 bytes each: the number of the material
// shader that draws it, its top bit set where the shader is translucent, then that shader's struct, its params, as the
// renderer packed them from the shader's reflection. Row 0 is the default material, which a material whose asset has
// not arrived is drawn with.
module "Scene/Materials";

import Core.Heap;

public static const uint GpuMaterialBytes = 64u;

// The shader word's bit for a translucent shader's row, C#'s GpuMaterial.TranslucentBit: drawn over the lit picture,
// not with the opaque surfaces.
public static const uint TranslucentBit = 0x80000000u;

uint LoadShaderWord(uint table, uint row)
{
    BufferRef materials = { table };
    return materials.Load<uint>(row * GpuMaterialBytes);
}

// The number of the material shader that draws the row.
public uint LoadMaterialShader(uint table, uint row) { return LoadShaderWord(table, row) & ~TranslucentBit; }

// Whether the row's shader is translucent, as glass is.
public bool IsTranslucent(uint table, uint row) { return (LoadShaderWord(table, row) & TranslucentBit) != 0u; }

// The row's params as the struct of the material shader that draws it.
public T LoadMaterialParams<T>(uint table, uint row)
{
    BufferRef materials = { table };
    return materials.Load<T>(row * GpuMaterialBytes + 4u);
}
