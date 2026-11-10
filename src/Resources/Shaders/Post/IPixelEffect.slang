// The pixel effect contract: a module implements it in a struct named for its file, whose fields are the effect's
// params by name, laid out as a material's are (Material/IMaterial). A Weather or a PostProcessing lists a material over
// that module, and a pass specialised to the struct runs each over every pixel of the picture, the weather's first, in
// order. An effect reads the picture as the ones before it left it, in linear light, and writes another, so it may read
// any pixel of it. The exposure and the tonemap are effects too, so what an effect is handed is the light as it is
// before the exposure, exposed after it, and ready for the screen after the tonemap.
//
// An effect that needs more than the picture keeps a target of its own: the struct declares how many levels it has,
// as `static const uint Levels = 2;` does, each half the size of the one before, the first the size the effect works
// at. Before Apply, Level runs over every texel of each level in turn, the first first; Apply then reads them. The
// target lasts from frame to frame, two of them swapping, so last frame's is the effect's history.
//
// An effect that blends frames declares `static const uint Jitters = 1;`: while it runs, the camera draws each frame
// through a different point in each pixel. It also upscales: it works at the output's size, the window's, which the
// picture may be smaller than, and so does every effect after it. The depth and the velocity stay the size the view is
// drawn at, so an effect finds their texel under a pixel from uv. One that keeps the picture's size declares
// `static const uint NoUpscaling = 1;`.
//
// An effect reads the frame, such as its time and the camera, with Core/Frame's GetFrame, and the sun and the sky
// with Scene/Lights' GetLights. One that asks what lies along a ray through the world, such as whether a point in
// the air sees the sun, asks Scene/Shadows' GetTransmittance, and declares `static const uint Rays = 1;`: on a GPU
// that doesn't trace rays, it doesn't run.
module "Post/IPixelEffect";

import Core.Heap;

public struct PixelEffectInputs
{
    // The pixel's place in what the pass writes, 0 to 1 from the top left, and in texels: the picture at the size the
    // effect works at, or the level of the effect's target that Level fills.
    public float2 uv;
    public uint2 pixel;
    public uint2 size;
    public uint level;
    // The pixel's opaque surface relative to the camera, in metres, or far along its ray where nothing was drawn, its
    // depth along the camera's view, 0 where nothing was drawn.
    public float3 position;
    public float viewDepth;
    // The nearest surface along the pixel's ray relative to the camera, in metres: the nearest pane of glass in front
    // of position where there is one, else position.
    public float3 nearestPosition;
    // The pixel's opaque surface: its unit normal in the world, its base colour, its roughness and its metalness, all 0
    // where nothing was drawn.
    public float3 normal;
    public float3 baseColor;
    public float roughness;
    public float metalness;
    // The picture as the effects before this one left it, the effect's target as this frame has filled it so far, and
    // as last frame left it, and the picture's reversed depth and velocity, each read at any texel, and a sampler that
    // filters between texels and clamps at the edges. The history holds nothing at a new view or size, or on the
    // effect's first frame. A velocity's xy is the step in texture coordinates to where the pixel's surface was last
    // frame, its z the surface's depth along last frame's view.
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
    // One texel of the effect's target at inputs.level; an effect without levels has none.
    float4 Level(PixelEffectInputs inputs) { return float4(0.0); }

    // The pixel's colour after the effect, from its colour before it.
    float3 Apply(float3 color, PixelEffectInputs inputs);
}
