// The frame's lights: the sun, the sky and the light table, every local light drawn this frame, C#'s GpuLight, 64 bytes
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
public static const uint LightsBytes = 84u;

public struct Lights
{
    // Towards the sun, unit; the sun's colour times intensity, in lux, before the air; and the open sky's radiance a
    // lux of the sun gives where no sky entity draws the sky.
    public float4 sunDirection;
    public float4 sunIlluminance;
    public float4 skyRadiance;
    // Metres above the ground.
    public float cameraAltitude;
    // The sky's tables and the sampler that clamps them, and whether a sky entity draws the sky.
    public uint transmittanceLut;
    public uint multiScatteringLut;
    public uint skyViewLut;
    public uint lutSampler;
    public uint hasSky;
    // The light table, the lights in it and their tiles.
    public uint table;
    public uint count;
    public uint tiles;
}

public Lights GetLights()
{
    BufferRef frame = { GetPassHeader().x };
    return frame.Load<Lights>(LightsOffset);
}

// Where the sun is in the picture, 0 to 1 from the top left and past that off its edges, in xy, and the cosine
// between the camera's view and the sun in z: 1 straight ahead, 0 or less beside or behind the camera, where xy
// means nothing.
public float3 GetSunOnScreen(FrameConstants frame, Lights lights)
{
    // The sun as a point infinitely far along its direction, whose clip w is its depth along the view: the cosine.
    float4 clip = mul(float4(lights.sunDirection.xyz, 0.0), frame.viewProjection);
    return float3(float2(clip.x, -clip.y) / clip.w * 0.5 + 0.5, clip.w);
}

// PROTO: the open sky's light where no sky entity draws the sky, as the shading draws a missing sky, until the
// sky's tables are handed on.
public float3 GetOpenSkyRadiance(Lights lights) { return lights.skyRadiance.rgb * lights.sunIlluminance.r; }

// What a sky is handed to answer for a direction from the camera.
public SkyInputs CreateSkyInputs(Lights lights, float3 direction, bool hasSunDisc)
{
    SkyInputs sky;
    sky.viewDirection = direction;
    sky.sunDirection = lights.sunDirection.xyz;
    sky.hasSunDisc = hasSunDisc;
    sky.sunIlluminance = lights.sunIlluminance.rgb;
    sky.altitude = lights.cameraAltitude;
    sky.transmittanceLut = lights.transmittanceLut;
    sky.multiScatteringLut = lights.multiScatteringLut;
    sky.skyViewLut = lights.skyViewLut;
    sky.sampler = lights.lutSampler;
    return sky;
}

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
