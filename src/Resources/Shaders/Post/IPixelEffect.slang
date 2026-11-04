// The post effect contract: a module implements it in a struct, a camera's PostProcessing names that module, and
// the post pass specialised to the structs runs each over every pixel of the picture, in linear light, after the
// exposure and before the tonemap.
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
    static float3 Apply(float3 color, PixelEffectInputs inputs);
}
