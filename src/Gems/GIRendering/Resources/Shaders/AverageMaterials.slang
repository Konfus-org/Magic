// Each material row as a ray hit reads it: the material evaluated once over its whole texture, so a texture reads its
// average colour, into the hit materials table. A ray may land on any material, and a pass can only call the one it is
// specialised to, so the entry is specialised to one material shader, numbered in the pass header's z, and fills the
// rows that shader draws. A thread a row; the header's x is the frame's constants, y the hit materials table and w the
// rows.
module "AverageMaterials";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Pack;
import Scene.Materials;
import Surface.Surface;
import Material.IMaterial;
import Trace.HitSurface;

[shader("compute")]
[numthreads(64, 1, 1)]
void AverageMaterials<M : IMaterial>(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    PassHeader header = GetPassHeader();
    uint row = thread.x;
    if (row >= header.w || LoadMaterialShader(frame.materials, row) != header.z)
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
    inputs.viewDirection = float3(0.0, 1.0, 0.0);
    M material = LoadMaterialParams<M>(frame.materials, row);
    SurfaceOutputs surface = material.Evaluate(inputs);

    GpuHitMaterial hit;
    hit.diffuse = surface.baseColor * (1.0 - surface.metalness);
    hit.transmission = PackUnorm4x8(float4(surface.baseColor * (1.0 - surface.alpha), 0.0));
    hit.emissive = surface.emissive;
    hit.padding1 = 0u;
    ArrayRef<GpuHitMaterial> table = { header.y };
    table[row] = hit;
}
