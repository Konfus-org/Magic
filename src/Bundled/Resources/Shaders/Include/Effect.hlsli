// What a renderer hands a post effect's shader (IRendering.Effect) at the start of its constants, ahead of the effect's
// own values: heap ids of the view's constants, of the image it is handed and of the texture of the same size and format
// it writes, a thread a pixel in groups of 8 by 8. The padding starts the values on 16 bytes, where a float4 among them
// lands as the C# struct lays it.
//
//   struct TonemapConstants
//   {
//       EffectImages effect;
//       float bias;
//   };

#ifndef MAGIC_EFFECT_HLSLI
#define MAGIC_EFFECT_HLSLI

struct EffectImages
{
    uint view;
    uint image;
    uint output;             // written
    uint padding;
};

#endif
