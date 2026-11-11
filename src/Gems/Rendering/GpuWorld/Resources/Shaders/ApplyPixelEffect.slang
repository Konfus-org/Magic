// A pixel effect's passes: each level of its target filled, then the effect applied over every pixel of the picture.
// A pass reads its GpuEffectPass from the table whose heap id is the pass header's y, at the row its z names, and the
// effect's struct from its row of the params table that names. The effect is handed each pixel as the effects before
// it left it, with how far its surface is; what it gives back is written into the other picture, for the next effect
// or the output.
module "ApplyPixelEffect";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Scene.Materials;
import Post.IPixelEffect;

// One pass of an effect, C#'s GpuEffectPass: the heap id of the table of the effects' params and the effect's row in
// it, the level of its target the pass fills, 0 for Apply, and the heap ids of the picture it reads, of what it writes,
// and of the effect's target and history, with whether the history holds last frame's target, and of the sampler that
// filters between texels.
struct GpuEffectPass
{
    uint rows;
    uint row;
    uint level;
    uint picture;
    uint output;
    uint target;
    uint history;
    uint hasHistory;
    uint sampler;
}

GpuEffectPass LoadPass()
{
    PassHeader header = GetPassHeader();
    ArrayRef<GpuEffectPass> passes = { header.y };
    return passes[header.z];
}

// What an effect is handed for a texel of what the pass writes, which is size texels across.
PixelEffectInputs CreateInputs(FrameConstants frame, GpuEffectPass pass, uint2 pixel, uint2 size)
{
    PixelEffectInputs inputs;
    inputs.size = size;
    inputs.pixel = pixel;
    inputs.uv = GetTexelCenterUv(pixel, size);
    inputs.level = pass.level;
    inputs.picture.id = pass.picture;
    inputs.target.id = pass.target;
    inputs.history.id = pass.history;
    inputs.hasHistory = pass.hasHistory != 0u;
    inputs.depth.id = frame.depth;
    inputs.velocity.id = frame.velocity;
    inputs.sampler.id = pass.sampler;

    // The depth under the texel's middle, in the picture's texels.
    float depth = inputs.depth.Load(GetTexelAt(inputs.uv, uint2(frame.width, frame.height)), 0u).x;
    inputs.viewDepth = depth > 0.0 ? ToViewDepth(depth, frame.near) : 0.0;
    return inputs;
}

[shader("compute")]
[numthreads(8, 8, 1)]
void FillEffectLevel<E : IPixelEffect>(uint3 thread : SV_DispatchThreadID)
{
    GpuEffectPass pass = LoadPass();
    StorageRef level = { pass.output, pass.level };
    uint2 size = level.GetSize();
    if (any(thread.xy >= size))
        return;

    E effect = LoadMaterialParams<E>(pass.rows, pass.row);
    level.Store(thread.xy, effect.Level(CreateInputs(GetFrame(), pass, thread.xy, size)));
}

[shader("compute")]
[numthreads(8, 8, 1)]
void ApplyPixelEffect<E : IPixelEffect>(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    uint2 size = uint2(frame.width, frame.height);
    if (any(thread.xy >= size))
        return;

    GpuEffectPass pass = LoadPass();
    E effect = LoadMaterialParams<E>(pass.rows, pass.row);
    PixelEffectInputs inputs = CreateInputs(frame, pass, thread.xy, size);
    float4 before = inputs.picture.Load(thread.xy, 0u);
    float3 after = effect.Apply(before.rgb, inputs);
    StorageRef output = { pass.output, 0u };
    output.Store(thread.xy, float4(after, before.a));
}
