// The pixel effect contract: a module implements it in a struct named for its file, whose fields are the effect's
// params by name, laid out as a material's are (Material/IMaterial). A Weather or a PostProcessing lists a material over
// that module, and the pass specialised to the struct runs each over every pixel of the picture, the weather's first,
// in order, in linear light, after the exposure and before the tonemap.
module "Post/IPixelEffect";

public struct PixelEffectInputs
{
    // The pixel's place in the output, 0 to 1 from the top left, and in texels.
    public float2 uv;
    public uint2 pixel;
    public uint2 size;
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
}

public interface IPixelEffect
{
    // The pixel's colour after the effect, from its colour before it.
    float3 Apply(float3 color, PixelEffectInputs inputs);
}
