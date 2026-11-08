// The post effect contract: a module implements it in a struct named for its file, whose fields are the effect's params
// by name, laid out as a material's are (Material/IMaterial). A PostProcessing lists a material over that module, and
// the post pass specialised to the struct runs each over every pixel of the picture, in order, in linear light, after
// the exposure and before the tonemap.
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
}

public interface IPixelEffect
{
    // The pixel's colour after the effect, from its colour before it.
    float3 Apply(float3 color, PixelEffectInputs inputs);
}
