// The pixel effect contract: a module implements it in a struct named for its file, whose fields are the effect's
// params by name, laid out as a material's are (Material/IMaterial). A PostProcessing lists a material over that
// module, and a pass specialised to the struct runs each over every pixel of the picture, in order. An effect reads
// the picture as the ones before it left it, in linear light, and writes another, so it may read any pixel of it. The
// exposure and the tonemap are effects too, so what an effect is handed is the light as it is before the exposure,
// exposed after it, and ready for the screen after the tonemap.
//
// An effect that needs more than the picture keeps a target of its own: the struct declares how many levels it has,
// as `static const uint Levels = 2;` does, each half the size of the one before, the first the picture's. Before
// Apply, Level runs over every texel of each level in turn, the first first. Apply then reads them. An effect that reads
// last frame's target declares `static const uint History = 1;`: its target lasts from frame to frame, two of them
// swapping, so last frame's is the effect's history. One that doesn't declare it has no history.
//
// An effect that blends frames declares `static const uint Jitters = 1;`: while it runs, the camera draws each frame
// through a different point in each pixel.
//
// An effect reads the frame, such as its time and the camera, with Core/Frame's GetFrame, and the sun and the sky
// with Scene/Lights' GetLights.
module "Post/IPixelEffect";

import Core.Heap;

public struct PixelEffectInputs
{
    // The pixel's place in what the pass writes, 0 to 1 from the top left, and in texels: the picture, or the level of
    // the effect's target that Level fills.
    public float2 uv;
    public uint2 pixel;
    public uint2 size;
    public uint level;
    // The pixel's opaque surface's depth along the camera's view, in metres, 0 where nothing was drawn.
    public float viewDepth;
    // The picture as the effects before this one left it, the effect's target as this frame has filled it so far, and
    // as last frame left it, and the picture's reversed depth and velocity, each read at any texel, and a sampler that
    // filters between texels and clamps at the edges. The history holds nothing at a new view or size, on the effect's
    // first frame, or for an effect that declares none. A velocity's xy is the step in texture coordinates to where the
    // pixel's surface was last frame, its z the surface's depth along last frame's view.
    public TextureRef picture;
    public TextureRef target;
    public TextureRef history;
    public bool hasHistory;
    public TextureRef depth;
    public TextureRef velocity;
    public SamplerRef sampler;
}

public interface IPixelEffect
{
    // One texel of the effect's target at inputs.level. An effect without levels has none.
    float4 Level(PixelEffectInputs inputs) { return float4(0.0); }

    // The pixel's colour after the effect, from its colour before it.
    float3 Apply(float3 color, PixelEffectInputs inputs);
}
