// The pixel effect contract: a module implements it in a struct named for its file, whose fields are the effect's
// params by name, laid out as a material's are (Material/IMaterial). A Weather or a PostProcessing lists a material over
// that module, and a pass specialised to the struct runs each over every pixel of the picture, the weather's first, in
// order, in linear light, after the exposure and before the tonemap. An effect reads the picture as the ones before it
// left it and writes another, so it may read any pixel of it.
//
// An effect that needs more than the picture keeps a target of its own: the struct declares how many levels it has,
// as `static const uint Levels = 2;` does, each half the size of the one before, the first the picture's. Before
// Apply, Level runs over every texel of each level in turn, the first first; Apply then reads them. The target lasts
// from frame to frame, two of them swapping, so last frame's is the effect's history.
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
    // What the picture was multiplied by to land near a middle grey.
    public float exposure;
    // Seconds the run has taken, for an effect that moves.
    public float time;
    // The pixel's surface relative to the camera, in metres, or far along its ray where nothing was drawn, and where the
    // camera is in the world.
    public float3 position;
    public float3 cameraPosition;
    // Towards the sun, unit, and the sun's and the sky's light, after the exposure as the colour is.
    public float3 sunDirection;
    public float3 sunRadiance;
    public float3 skyRadiance;
    // The pixel's surface: its unit normal in the world, its base colour, its roughness and its metalness, all 0 where
    // nothing was drawn.
    public float3 normal;
    public float3 baseColor;
    public float roughness;
    public float metalness;
    // The picture as the effects before this one left it, the effect's target as this frame has filled it so far, and
    // as last frame left it, all before the exposure, and the picture's reversed depth and screen-space velocity, each
    // read at any texel. The history holds nothing at a new view or size, or on the effect's first frame.
    public TextureRef picture;
    public TextureRef target;
    public TextureRef history;
    public bool hasHistory;
    public TextureRef depth;
    public TextureRef velocity;
}

public interface IPixelEffect
{
    // One texel of the effect's target at inputs.level, before the exposure; an effect without levels has none.
    float4 Level(PixelEffectInputs inputs) { return float4(0.0); }

    // The pixel's colour after the effect, from its colour before it.
    float3 Apply(float3 color, PixelEffectInputs inputs);
}
