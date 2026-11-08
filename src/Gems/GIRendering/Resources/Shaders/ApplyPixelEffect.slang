// One pixel effect over every pixel of the lit picture, in place: the effect's struct is loaded from its row of the
// table whose heap id is the pass header's y, at the row its z names, and handed each pixel after the exposure, with
// where its surface is and the sun's and the sky's light; what it gives back is written before the exposure again, for
// the next effect or the output.
module "ApplyPixelEffect";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Scene.Materials;
import Post.IPixelEffect;

// The reversed depth a pixel where nothing was drawn is put at: far along its ray, short of infinity.
static const float FarDepth = 1e-6;

[shader("compute")]
[numthreads(8, 8, 1)]
void ApplyPixelEffect<E : IPixelEffect>(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    if (any(thread.xy >= uint2(frame.width, frame.height)))
        return;

    PassHeader header = GetPassHeader();
    E effect = LoadMaterialParams<E>(header.y, header.z);
    TextureRef depth = { frame.depth };
    PixelEffectInputs inputs;
    inputs.size = uint2(frame.width, frame.height);
    inputs.pixel = thread.xy;
    inputs.uv = (float2(thread.xy) + 0.5) / float2(inputs.size);
    inputs.exposure = frame.exposure;
    inputs.time = frame.timeSec;
    inputs.position = ReconstructRelative(frame, inputs.uv, max(depth.Load(thread.xy, 0u).x, FarDepth));
    inputs.cameraPosition = frame.cameraPosition.xyz;
    inputs.sunDirection = frame.sunDirection.xyz;
    inputs.sunRadiance = frame.sunRadiance.rgb * frame.exposure;
    // PROTO: the sky's light as the shading draws a missing sky, until the sky's tables are handed on.
    inputs.skyRadiance = frame.skyRadiance.rgb * frame.sunRadiance.r * frame.exposure;

    StorageRef picture = { frame.hdr, 0u };
    float4 before = picture.Load(thread.xy);
    float3 after = effect.Apply(before.rgb * frame.exposure, inputs);
    picture.Store(thread.xy, float4(after / frame.exposure, before.a));
}
