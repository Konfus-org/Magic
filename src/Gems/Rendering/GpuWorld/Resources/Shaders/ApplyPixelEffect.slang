// A pixel effect's passes: each level of its target filled, then the effect applied over every pixel of what it writes.
// A pass reads its GpuEffectPass from the table whose heap id is the pass header's y, at the row its z names, and the
// effect's struct from its row of the params table that names. The effect is handed each pixel as the effects before
// it left it, with where its surface and the nearest pane of glass in front of it are; what it gives back is written
// into the other picture, for the next effect or the output.
module "ApplyPixelEffect";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Core.Pack;
import Scene.Materials;
import Post.IPixelEffect;

// The reversed depth a pixel where nothing was drawn is put at: far along its ray, short of infinity.
static const float FarDepth = 1e-6;

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
    inputs.uv = (float2(pixel) + 0.5) / float2(size);
    inputs.level = pass.level;
    inputs.picture.id = pass.picture;
    inputs.target.id = pass.target;
    inputs.history.id = pass.history;
    inputs.hasHistory = pass.hasHistory != 0u;
    inputs.depth.id = frame.depth;
    inputs.velocity.id = frame.velocity;
    inputs.sampler.id = pass.sampler;

    // The surface under the texel's middle, in the picture's texels.
    uint2 pictureSize = uint2(frame.width, frame.height);
    uint2 texel = min(uint2(inputs.uv * float2(pictureSize)), pictureSize - 1u);
    float depth = inputs.depth.Load(texel, 0u).x;
    inputs.position = ReconstructRelative(frame, inputs.uv, max(depth, FarDepth));
    inputs.viewDepth = depth > 0.0 ? ToViewDepth(depth, frame.near) : 0.0;

    // The nearer of that surface and the nearest pane of glass in front of it, in reversed depth the greater.
    TextureRef glassDepth = { frame.glassDepth };
    float nearest = max(max(depth, glassDepth.Load(texel, 0u).x), FarDepth);
    inputs.nearestPosition = ReconstructRelative(frame, inputs.uv, nearest);

    TextureRef normals = { frame.gbufferNormal };
    TextureRef bases = { frame.gbufferBase };
    TextureRef surfaces = { frame.gbufferSurface };
    bool isDrawn = depth > 0.0;
    float4 base = isDrawn ? bases.Load(texel, 0u) : float4(0.0);
    inputs.normal = isDrawn ? DecodeOctahedral(normals.Load(texel, 0u).xy * 2.0 - 1.0) : float3(0.0);
    inputs.baseColor = base.rgb;
    inputs.metalness = base.a;
    inputs.roughness = isDrawn ? surfaces.Load(texel, 0u).x : 0.0;
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
    GpuEffectPass pass = LoadPass();
    StorageRef output = { pass.output, 0u };
    uint2 size = output.GetSize();
    if (any(thread.xy >= size))
        return;

    E effect = LoadMaterialParams<E>(pass.rows, pass.row);
    PixelEffectInputs inputs = CreateInputs(GetFrame(), pass, thread.xy, size);

    // The picture's texel under the pixel: the pixel itself where the picture is the size of what the pass writes.
    uint2 pictureSize = inputs.picture.GetSize(0u);
    float4 before = inputs.picture.Load(min(uint2(inputs.uv * float2(pictureSize)), pictureSize - 1u), 0u);
    float3 after = effect.Apply(before.rgb, inputs);
    output.Store(thread.xy, float4(after, before.a));
}
