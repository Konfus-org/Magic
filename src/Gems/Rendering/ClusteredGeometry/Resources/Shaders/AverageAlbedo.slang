// Each material row's diffuse colour, which a reflective shadow map draws: the material evaluated once over its whole
// texture, so a texture reads its average colour, as a ray hit reads a material. A pass can only call the material it is
// specialised to, so the entry is specialised to one material shader, numbered in the pass header's z, and fills the
// rows that shader draws. A thread a row. The header's x is the frame's constants, y the table of colours, one float4 a
// row, and w the rows.
module "AverageAlbedo";

import Core.Heap;
import Core.Pass;
import Scene.Materials;
import Surface.Surface;
import Material.IMaterial;
import Scene.Models;

[shader("compute")]
[numthreads(64, 1, 1)]
void AverageAlbedo<M : IMaterial>(uint3 thread : SV_DispatchThreadID)
{
    Models models = GetModels();
    PassHeader header = GetPassHeader();
    uint row = thread.x;
    if (row >= header.w || LoadMaterialShader(models.materials, row) != header.z)
        return;

    // A surface facing up whose texture coordinates change by a whole texture over a pixel: a texture sampled with
    // that reads its smallest level.
    SurfaceInputs inputs;
    inputs.position = float3(0.0);
    inputs.normal = float3(0.0, 1.0, 0.0);
    inputs.tangent = float3(1.0, 0.0, 0.0);
    inputs.tangentSign = 1.0;
    inputs.uv = float2(0.5);
    inputs.uvDdx = float2(1.0, 0.0);
    inputs.uvDdy = float2(0.0, 1.0);
    M material = LoadMaterialParams<M>(models.materials, row);
    SurfaceOutputs surface = material.Evaluate(inputs);

    ArrayRef<float4> table = { header.y };
    table[row] = float4(surface.baseColor * (1.0 - surface.metalness) * surface.alpha, 1.0);
}
