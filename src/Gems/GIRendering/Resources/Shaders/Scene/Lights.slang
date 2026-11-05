// The light table and its tiles: every local light drawn this frame, C#'s GpuLight, 64 bytes each, and for each tile of
// the screen a bit per light that reaches what the tile shows, so a pixel loops over its tile's few lights and not the
// thousand in the world.
module "Scene/Lights";

import Core.Heap;

public static const uint LightPoint = 0u;
public static const uint LightSpot = 1u;
public static const uint LightRect = 2u;

// The most lights a frame draws, and the words of bits a tile keeps for them: C#'s LightTiles.
public static const uint MaxLights = 1024u;
public static const uint LightBuckets = MaxLights / 32u;

// The pixels a tile covers along a side.
public static const uint LightTileSize = 32u;

public struct GpuLight
{
    // In the world.
    public float3 position;
    public float range;
    // Along the light's +Z, unit.
    public float3 direction;
    // The source's radius for a point light, in metres.
    public float radius;
    // Colour times intensity: candela.
    public float3 radiance;
    public uint type;
    public float cosOuter;
    public float cosInner;
    public float width;
    public float height;
}

public GpuLight LoadLight(uint table, uint index)
{
    ArrayRef<GpuLight> lights = { table };
    return lights[index];
}

// The sphere around everything a light reaches: its range, and a rect light's half diagonal past it.
public float GetLightReach(GpuLight light)
{
    return light.range + (light.type == LightRect ? 0.5 * length(float2(light.width, light.height)) : 0.0);
}

// The first of a tile's words of bits in the tile table.
public uint GetLightTileOffset(uint2 tile, uint tilesAcross) { return (tile.y * tilesAcross + tile.x) * LightBuckets; }
