// The air between the camera and what it sees, as whatever fills it leaves it for a frame: a volume over the view, out
// to a distance, of the light the air scatters towards the camera and how much of what is behind gets through, and past
// it a haze thinning with height, lit as if nothing stood in the light's way. It is a row of the frame's buffer, C#'s
// GpuAir, every id 0 where nothing filled it this frame, and the air then changes nothing.
module "Light/Air";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Light.Indirect;
import Scene.Lights;

// Where the air is in the frame's buffer, right after the indirect light, and its bytes: C#'s GpuAir.
public static const uint AirOffset = IndirectLightOffset + IndirectLightBytes;
public static const uint AirBytes = 40u;

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
