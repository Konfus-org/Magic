// One post effect over every pixel of the lit picture, in place: the effect's struct is loaded from its row of the
// table whose heap id is the pass header's y, at the row its z names, and handed each pixel after the exposure; what it
// gives back is written before the exposure again, for the next effect or the output.
module "ApplyPostEffect";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Scene.Materials;
import Post.IPixelEffect;

[shader("compute")]
[numthreads(8, 8, 1)]
void ApplyPostEffect<E : IPixelEffect>(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    if (any(thread.xy >= uint2(frame.width, frame.height)))
        return;

    PassHeader header = GetPassHeader();
    E effect = LoadMaterialParams<E>(header.y, header.z);
    PixelEffectInputs inputs;
    inputs.size = uint2(frame.width, frame.height);
    inputs.pixel = thread.xy;
    inputs.uv = (float2(thread.xy) + 0.5) / float2(inputs.size);
    inputs.exposure = frame.exposure;
    inputs.time = frame.timeSec;

    StorageRef picture = { frame.hdr, 0u };
    float4 before = picture.Load(thread.xy);
    float3 after = effect.Apply(before.rgb * frame.exposure, inputs);
    picture.Store(thread.xy, float4(after / frame.exposure, before.a));
}
