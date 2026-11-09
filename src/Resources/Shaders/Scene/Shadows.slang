// The shadow mask: for each pixel, how much of each light lighting it reaches it, 8 bits a light in 4 words. The sun
// is the first slot and the first lights the pixel's tile lists, in the order of their bits, the rest; a light past
// the last slot is unshadowed. Beside it, the sun's tiles: a bit for each pixel of 8 by 4 that its ray reached the sun
// from, a word a tile, row by row; and the sun's tint: the colour of the sun's light that reaches the pixel through
// glass over how bright it is, which the sun's slot holds, so the two together give the light coloured.
module "Scene/Shadows";

import Core.Heap;
import Core.Pass;
import Scene.Lights;

// Where the shadows are in the frame's buffer, right after the lights, and their bytes: C#'s GpuShadows.
public static const uint ShadowsOffset = LightsOffset + LightsBytes;
public static const uint ShadowsBytes = 16u;

public struct Shadows
{
    // The denoised mask shading reads, 0 on a GPU that traces no rays, where nothing is shadowed; the mask as the rays
    // left it, and the sun's tiles, for the denoiser; and the sun's tint.
    public uint mask;
    public uint traced;
    public uint sunTiles;
    public uint sunTint;
}

public Shadows GetShadows()
{
    BufferRef frame = { GetPassHeader().x };
    return frame.Load<Shadows>(ShadowsOffset);
}

public static const uint ShadowSlots = 16u;

public static const uint2 SunTileSize = uint2(8u, 4u);

public uint GetSunTileIndex(uint2 tile, uint width) { return tile.y * ((width + SunTileSize.x - 1u) / SunTileSize.x) + tile.x; }

public uint GetSunTileBit(uint2 pixel) { return 1u << ((pixel.y % SunTileSize.y) * SunTileSize.x + pixel.x % SunTileSize.x); }

// A pixel's words, all lit where there is no mask.
public uint4 LoadShadowMask(uint mask, uint2 pixel, uint width)
{
    if (mask == 0u)
        return uint4(0xFFFFFFFFu);

    ArrayRef<uint4> words = { mask };
    return words[pixel.y * width + pixel.x];
}

// The colour the sun's light reaching a pixel takes on through glass: white where there is no tint.
public float3 LoadSunTint(uint tint, uint2 pixel)
{
    if (tint == 0u)
        return float3(1.0);

    TextureRef tints = { tint };
    return tints.Load(pixel, 0u).rgb;
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
