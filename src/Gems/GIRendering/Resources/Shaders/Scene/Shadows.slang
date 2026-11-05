// The shadow mask: for each pixel, how much of each light lighting it reaches it, 8 bits a light in 4 words. The sun
// is the first slot and the first lights the pixel's tile lists, in the order of their bits, the rest; a light past
// the last slot is unshadowed.
module "Scene/Shadows";

import Core.Heap;

public static const uint ShadowSlots = 16u;

// A pixel's words, all lit where there is no mask.
public uint4 LoadShadowMask(uint mask, uint2 pixel, uint width)
{
    if (mask == 0u)
        return uint4(0xFFFFFFFFu);

    ArrayRef<uint4> words = { mask };
    return words[pixel.y * width + pixel.x];
}

public void StoreShadowMask(uint mask, uint2 pixel, uint width, uint4 value)
{
    ArrayRef<uint4> words = { mask };
    words[pixel.y * width + pixel.x] = value;
}

// How much of a slot's light reaches the pixel, from 0 to 1.
public float GetShadow(uint4 words, uint slot)
{
    if (slot >= ShadowSlots)
        return 1.0;

    return float((words[slot / 4u] >> ((slot % 4u) * 8u)) & 0xFFu) / 255.0;
}

// The words with a slot set to how much of its light reaches the pixel; every slot starts at 0.
public uint4 EncodeShadow(uint4 words, uint slot, float shadow)
{
    uint4 result = words;
    result[slot / 4u] |= uint(saturate(shadow) * 255.0 + 0.5) << ((slot % 4u) * 8u);
    return result;
}
