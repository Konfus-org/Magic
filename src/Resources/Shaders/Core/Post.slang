// What a renderer hands a post effect. A pass that reads its neighbours (IRendering.Effect) is a compute entry point
// whose uniform parameters are the pass header, PostImages, then the effect's own values, a thread a pixel in groups of
// 8 by 8. An effect that changes each pixel by itself is a struct implementing IPixelEffect, named like its file, its
// fields its values: the renderer runs a run of them as one pass, each pixel through each in order (Then).
//
//   public struct Tonemap : IPixelEffect
//   {
//       float bias;
//       public float3 Apply(float3 color, PixelInputs input) { ... }
//   }

module "Core/Post";

__exported import Core.Pass;

// The image the effect is handed, and the texture of its size and format it writes.
public struct PostImages
{
    TextureRef image;
    TextureRef output;       // written, at level 0
}

// What a per-pixel effect is handed besides the pixel's colour.
public struct PixelInputs
{
    uint2 pixel;             // from the image's top left
    float2 uv;               // the pixel's centre, 0 to 1 across the image
    FrameConstants frame;
}

public interface IPixelEffect
{
    // The pixel's colour after the effect, from its colour before.
    float3 Apply(float3 color, PixelInputs input);
}

// Two effects one after the other, which a run of more nests: Then<A, Then<B, C>>. Their values are laid out first's,
// then second's.
public struct Then<First : IPixelEffect, Second : IPixelEffect> : IPixelEffect
{
    First first;
    Second second;

    public float3 Apply(float3 color, PixelInputs input)
    {
        return second.Apply(first.Apply(color, input), input);
    }
}
