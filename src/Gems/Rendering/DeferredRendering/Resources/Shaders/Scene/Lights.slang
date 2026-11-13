// The frame's lights: the sun, the sky's tables and the light table, every local light drawn this frame, C#'s GpuLight, 64 bytes
// each, with for each tile of the screen a bit per light that reaches what the tile shows, so a pixel loops over its
// tile's few lights and not the thousand in the world.
module "Scene/Lights";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Scene.Models;
import Sky.ISky;

// The sun's angular radius as it looks from the Earth: how wide a disc its shadow rays aim through.
public static const float SunAngularRadius = 0.00465;

// Where the lights are in the frame's buffer, right after the world as rays see it, and their bytes: C#'s GpuLights.
public static const uint LightsOffset = TracedWorldOffset + TracedWorldBytes;
public static const uint LightsBytes = 68u;

public struct Lights
{
    // Towards the sun, unit. The sun's colour times intensity, in lux, before the air. The open sky's radiance a
    // lux of the sun gives where no sky entity draws the sky.
    public float4 sunDirection;
    public float4 sunIlluminance;
    public float4 skyRadiance;
    // Metres above the ground.
    public float cameraAltitude;
    // The sampler that clamps the sky's tables.
    public uint lutSampler;
    // The light table, the lights in it and their tiles.
    public uint table;
    public uint count;
    public uint tiles;
}

public Lights GetLights() { return LoadFrameRow<Lights>(LightsOffset); }

// Where the sky's tables are in the frame's buffer, right after the lights, and their bytes: C#'s GpuSkyTables.
public static const uint SkyTablesOffset = LightsOffset + LightsBytes;
public static const uint SkyTablesBytes = 12u;

// The heap ids of the sky's three tables, all 0 where no sky entity draws the sky.
public struct SkyTables
{
    public uint transmittance;
    public uint multiScattering;
    public uint view;
}

public SkyTables GetSkyTables() { return LoadFrameRow<SkyTables>(SkyTablesOffset); }

// Whether a sky entity draws the sky: its tables are made.
public bool HasSky(SkyTables tables) { return tables.transmittance != 0u; }

// PROTO: the open sky's light where no sky entity draws the sky, as the shading draws a missing sky, until the
// sky's tables are handed on.
public float3 GetOpenSkyRadiance(Lights lights) { return lights.skyRadiance.rgb * lights.sunIlluminance.r; }

// The sun's light where it reaches the camera: through the sky's air where a sky entity draws the sky, else as it is.
public float3 GetSunIlluminance<S : ISky>(Lights lights)
{
    SkyTables tables = GetSkyTables();
    if (!HasSky(tables))
        return lights.sunIlluminance.rgb;

    return S.GetSunIlluminance(CreateSkyInputs(lights, tables, lights.sunDirection.xyz, false));
}

// The sky along a direction, without the sun's disc, whose light the sun's own rays bring.
public float3 GetSkyRadiance<S : ISky>(Lights lights, float3 direction)
{
    SkyTables tables = GetSkyTables();
    if (!HasSky(tables))
        return GetOpenSkyRadiance(lights);

    return S.Evaluate(CreateSkyInputs(lights, tables, direction, false));
}

// What a sky is handed to answer for a direction from the camera.
public SkyInputs CreateSkyInputs(Lights lights, SkyTables tables, float3 direction, bool hasSunDisc)
{
    SkyInputs sky;
    sky.viewDirection = direction;
    sky.sunDirection = lights.sunDirection.xyz;
    sky.hasSunDisc = hasSunDisc;
    sky.sunIlluminance = lights.sunIlluminance.rgb;
    sky.altitude = lights.cameraAltitude;
    sky.transmittanceLut = tables.transmittance;
    sky.skyViewLut = tables.view;
    sky.sampler = lights.lutSampler;
    return sky;
}

// The shape of a GpuLight, C#'s LightType.
public enum LightType : uint
{
    Point = 0,
    Spot = 1,
    Rect = 2,
}

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
    public LightType type;
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
    return light.range + (light.type == LightType.Rect ? 0.5 * length(float2(light.width, light.height)) : 0.0);
}

// The first of a tile's words of bits in the tile table, for a picture width pixels across.
public uint GetLightTileOffset(uint2 tile, uint width)
{
    return (tile.y * ((width + LightTileSize - 1u) / LightTileSize) + tile.x) * LightBuckets;
}
