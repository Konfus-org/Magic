// The material table: a row per material slot drawn, C#'s GpuMaterial, 64 bytes each: the shader word, then the
// struct of the material shader that draws the row, its params, as the renderer packed them from the shader's
// reflection. The shader word holds the number of that shader in bits 0 to 11, the index of the row's failure in the
// failure table in bits 12 to 30, 0 where nothing failed (Scene/Failures), and the translucent bit in bit 31. Row 0 is
// the default material, which a material whose asset has not arrived is drawn with.
module "Scene/Materials";

import Core.Heap;

public static const uint GpuMaterialBytes = 64u;

// The shader word's bit for a translucent shader's row, C#'s GpuMaterial.TranslucentBit: drawn over the lit picture,
// not with the opaque surfaces.
public static const uint TranslucentBit = 0x80000000u;

// The shader word's bits for the material shader's number, and where its failure index starts and the bits it takes.
// A project numbers each material shader it draws once, and each takes a pipeline and a resolve pass a frame, so
// 4096 is far more than one draws. The failure table has a row for each thing that failed.
static const uint MaterialShaderMask = 0xFFFu;
static const uint FailureIndexShift = 12u;
static const uint FailureIndexMask = 0x7FFFFu;

uint LoadShaderWord(uint table, uint row)
{
    BufferRef materials = { table };
    return materials.Load<uint>(row * GpuMaterialBytes);
}

// The number of the material shader that draws the row.
public uint LoadMaterialShader(uint table, uint row) { return LoadShaderWord(table, row) & MaterialShaderMask; }

// The index of the row's failure in the failure table, 0 where nothing failed.
public uint LoadFailureIndex(uint table, uint row)
{
    return (LoadShaderWord(table, row) >> FailureIndexShift) & FailureIndexMask;
}

// Whether the row's shader is translucent, as glass is.
public bool IsTranslucent(uint table, uint row) { return (LoadShaderWord(table, row) & TranslucentBit) != 0u; }

// The row's params as the struct of the material shader that draws it.
public T LoadMaterialParams<T>(uint table, uint row)
{
    BufferRef materials = { table };
    return materials.Load<T>(row * GpuMaterialBytes + 4u);
}
