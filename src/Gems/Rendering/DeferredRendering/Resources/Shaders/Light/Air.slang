// The air between the camera and what it sees, as whatever fills it leaves it for a frame: a volume over the view, out
// to a distance, of the light the air scatters towards the camera and how much of what is behind gets through, past
// it a haze thinning with height, lit as if nothing stood in the light's way, and the clouds over it all. It is a row
// of the frame's buffer, C#'s GpuAir, every id 0 where nothing filled it this frame, and the air then changes nothing.
module "Light/Air";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Core.Random;
import Light.Indirect;
import Scene.Lights;

// Where the air is in the frame's buffer, right after the indirect light, and its bytes: C#'s GpuAir.
public static const uint AirOffset = IndirectLightOffset + IndirectLightBytes;
public static const uint AirBytes = 60u;

public struct Air
{
    // The volume: across and down the view as the picture is, and its slices away from the camera, each holding in rgb
    // the light scattered towards the camera up to its far side and in alpha how much of what is behind gets through.
    public uint volume;
    public uint sampler;
    // The metres the volume reaches.
    public float distance;
    // The haze: how much of the light a metre of it scatters at its height, the metres above that at which it is half
    // as thick, how much it scatters onward, from -1 to 1, and its colour.
    public float density;
    public float height;
    public float halfHeight;
    public float anisotropy;
    public float3 color;
    // The sky's clouds as seen from the camera, 0 where there are none: in rgb the light they send towards the camera
    // and in alpha how much of the sky behind they hide.
    public uint clouds;
    // How much of the sky they cover, from 0 to 1, the height of their base, and the wind that carries them across the
    // ground, in metres per second.
    public float cloudCover;
    public float cloudHeight;
    public float2 wind;
}

// The frame's air; every id 0 where nothing filled it.
public Air GetAir()
{
    BufferRef frame = { GetPassHeader().x };
    return frame.Load<Air>(AirOffset);
}

// The volume's slices away from the camera.
public static const uint AirSlices = 64u;

// How fast the volume's slices thicken away from the camera: each is 2^SliceCurve times as deep as the first by the
// last.
static const float SliceCurve = 6.0;

// Where a distance from the camera is along the volume, 0 at the camera and 1 at its reach, and back.
public float ToAirDepth(float distance, float reach)
{
    return log2(1.0 + distance / reach * (exp2(SliceCurve) - 1.0)) / SliceCurve;
}

public float ToAirDistance(float depth, float reach) { return reach * (exp2(SliceCurve * depth) - 1.0) / (exp2(SliceCurve) - 1.0); }

// How much of the light the air scatters goes off at an angle from its path, per steradian, by the cosine of that
// angle: evenly at an anisotropy of 0, towards 1 mostly onward, by the Henyey-Greenstein phase function (Henyey and
// Greenstein, "Diffuse radiation in the galaxy", 1941).
public float GetAirPhase(float anisotropy, float cosine)
{
    float g = clamp(anisotropy, -0.99, 0.99);
    return (1.0 - g * g) / (4.0 * Pi * pow(max(1.0 + g * g - 2.0 * g * cosine, 1e-4), 1.5));
}

// How thick the haze is at a height in the world, as a part of a metre's light it scatters.
public float GetHazeDensity(Air air, float height)
{
    return air.density * exp(-0.693147 / max(air.halfHeight, 0.01) * (height - air.height));
}

// The light the air scatters towards the camera from a unit direction, per part of it scattered: its colour times the
// sky's light, evenly, and the sun's by the angle to it, each as much of it as gets through, by colour.
public float3 GetAirRadiance(Air air, Lights lights, float3 direction, float3 skyThrough, float3 sunThrough)
{
    float phase = GetAirPhase(air.anisotropy, dot(direction, lights.sunDirection.xyz));
    return air.color * (skyThrough * GetOpenSkyRadiance(lights) + sunThrough * lights.sunIlluminance.rgb * phase);
}

// Under this much change of the haze's exponent along a ray, the haze is taken as level, where the closed form divides
// by nothing.
static const float LevelRise = 1e-4;

// How much of the haze lies along a unit direction from the camera between two distances: its density integrated in
// closed form, falling off exponentially with height (Wenzel, "Real-Time Atmospheric Effects in Games", 2006).
float GetHazeDepth(Air air, float3 camera, float3 direction, float near, float far)
{
    float falloff = 0.693147 / max(air.halfHeight, 0.01);
    float rise = direction.y * falloff;
    float atNear = GetHazeDensity(air, camera.y + direction.y * near);
    float span = far - near;
    // Clamped going down only, where exp(-rise) would overflow; going up it falls to 0 and the form stays exact.
    float risen = max(rise * span, -80.0);
    return atNear * (abs(risen) > LevelRise ? span * (1.0 - exp(-risen)) / risen : span);
}

// A colour seen through the air at a camera-relative position: dimmed by what the air takes from it and lit by what it
// scatters towards the camera, read from the volume as far as it reaches, and past it from the haze in closed form,
// unshadowed. Unchanged where nothing filled the air this frame.
public float3 ApplyAir(float3 color, float3 position)
{
    Air air = GetAir();
    if (air.volume == 0u)
        return color;

    FrameConstants frame = GetFrame();
    float distance = length(position);
    float3 direction = position / max(distance, Epsilon);
    if (distance > air.distance)
    {
        Lights lights = GetLights();
        float through = exp(-GetHazeDepth(air, frame.cameraPosition.xyz, direction, air.distance, distance));
        color = color * through + GetAirRadiance(air, lights, direction, float3(1.0), float3(1.0)) * (1.0 - through);
    }

    // Each slice holds the air up to its far side, so a point reads between the slices around it, and before the first
    // slice's far side, towards the clear air at the camera.
    float4 clip = mul(float4(position, 1.0), frame.viewProjection);
    float slice = ToAirDepth(min(distance, air.distance), air.distance) * float(AirSlices);
    VolumeRef volume = { air.volume };
    SamplerRef sampler = { air.sampler };
    float3 at = float3(ToUv(clip.xy / max(clip.w, Epsilon)), (slice - 0.5) / float(AirSlices));
    float4 near = lerp(float4(0.0, 0.0, 0.0, 1.0), volume.SampleLevel(sampler, at, 0.0), saturate(slice));
    return color * near.a + near.rgb;
}

// The metres from the clouds' base to their top.
public static const float CloudThickness = 2000.0;

// The metres across the clouds' largest shapes.
static const float CloudScale = 4000.0;

// How much of the sun's light the thickest cloud lets through, as an exponent: e^-3, a twentieth.
static const float CloudShadowDepth = 3.0;

// Noise in [0, 1] that varies smoothly over the ground, a value a whole cell, blended between cells.
float GetValueNoise(float2 at)
{
    int2 cell = int2(floor(at));
    float2 f = smoothstep(0.0, 1.0, frac(at));
    float corners[4];
    for (uint i = 0u; i < 4u; i++)
    {
        corners[i] = ToUnitFloat(GetHash(uint3(uint2(cell + int2(i & 1u, i >> 1u)), 0x9E37u)).x);
    }

    return lerp(lerp(corners[0], corners[1], f.x), lerp(corners[2], corners[3], f.x), f.y);
}

// How much of the sky over a point of the ground the clouds fill at a time, from 0 to 1: three octaves of noise carried
// by the wind, cut off where the cover leaves the sky clear.
public float GetCloudCoverage(Air air, float2 ground, float time)
{
    float2 at = (ground - air.wind * time) / CloudScale;
    float noise = 0.5 * GetValueNoise(at) + 0.3 * GetValueNoise(at * 2.03 + 17.0)
        + 0.2 * GetValueNoise(at * 4.01 + 43.0);
    return saturate((noise - (1.0 - air.cloudCover)) / max(air.cloudCover, 0.01));
}

// How much of the sun's light reaches a point in the world through the clouds, read where the way to the sun crosses
// their middle; 1 where there are none.
public float GetCloudShadow(Air air, float3 world, float3 toSun, float time)
{
    if (air.cloudCover <= 0.0 || toSun.y <= 0.0)
        return 1.0;

    float rise = air.cloudHeight + 0.5 * CloudThickness - world.y;
    if (rise <= 0.0)
        return 1.0;

    float2 under = world.xz + toSun.xz * (rise / toSun.y);
    return exp(-CloudShadowDepth * GetCloudCoverage(air, under, time));
}

// Where a direction from the camera is on the clouds' picture of the sky: the sky's half folded onto a square, the
// zenith at its centre and the horizon on its edge; and back.
public float2 ToCloudUv(float3 direction)
{
    float3 up = float3(direction.x, max(direction.y, 0.0), direction.z);
    float2 diamond = up.xz / max(abs(up.x) + up.y + abs(up.z), Epsilon);
    return float2(diamond.x + diamond.y, diamond.x - diamond.y) * 0.5 + 0.5;
}

public float3 FromCloudUv(float2 uv)
{
    float2 square = uv * 2.0 - 1.0;
    float2 diamond = float2(square.x + square.y, square.x - square.y) * 0.5;
    return normalize(float3(diamond.x, 1.0 - abs(diamond.x) - abs(diamond.y), diamond.y));
}

// The sky along a unit direction from the camera with the clouds in front of it; unchanged where there are none.
public float3 ApplyClouds(float3 sky, float3 direction)
{
    Air air = GetAir();
    if (air.clouds == 0u || direction.y <= 0.0)
        return sky;

    TextureRef clouds = { air.clouds };
    SamplerRef sampler = { air.sampler };
    float4 seen = clouds.SampleLevel(sampler, ToCloudUv(direction), 0.0);
    return sky * (1.0 - seen.a) + seen.rgb;
}
