// A gradient written through the heap: red along x, green along y, a quarter blue, opaque. The pass header's x is the
// target texture's id and y and z its size.
module "Gradient";
// Every module of the core library, so the check compiles the library as well as the entry.
import Core.Heap;
import Core.Pass;
import Core.Math;
import Core.Color;
import Core.Pack;
import Core.Sampling;
import Core.Random;

[shader("compute")]
[numthreads(8, 8, 1)]
void FillGradient(uint3 thread : SV_DispatchThreadID)
{
    PassHeader header = GetPassHeader();
    uint2 size = uint2(header.y, header.z);
    if (any(thread.xy >= size))
        return;

    StorageRef target = { header.x, 0u };
    float2 uv = float2(thread.xy) / float2(size - 1u);
    target.Store(thread.xy, float4(uv, 0.25, 1.0));
}
