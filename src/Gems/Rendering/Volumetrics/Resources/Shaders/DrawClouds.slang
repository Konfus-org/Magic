// The sky's clouds as seen from the camera, drawn into a picture of the sky's half that Light/Air's ApplyClouds
// reads: a slab from the clouds' base up CloudThickness, filled where Light/Air's coverage says, carved by finer noise,
// marched along each texel's direction and lit by the sun through the cloud towards it and by the open sky, each frame
// a sixteenth of the texels, in turn (Schneider, "The Real-Time Volumetric Cloudscapes of Horizon Zero Dawn", 2015).
// The pass header's x is the frame's buffer, which holds the lights and the air, y the picture written, and z the side
// of the blocks of texels of which a thread draws one a frame, blended with what the texel held: 1 draws all of it
// anew.
module "DrawClouds";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Core.Random;
import Scene.Lights;
import Light.Air;

// The steps along a texel's direction through the slab, and towards the sun from each.
static const uint Steps = 32u;
static const uint SunSteps = 3u;

// The metres between the steps towards the sun.
static const float SunStep = 150.0;

// The farthest the clouds are drawn, in metres along the direction, and the most of the slab one direction crosses.
static const float CloudReach = 40000.0;
static const float CrossingReach = 12000.0;

// How much of the light a metre of the thickest cloud takes.
static const float CloudExtinction = 0.03;

// The metres across the noise that carves the clouds' edges.
static const float DetailScale = 600.0;

// How much of the light a metre of the clouds takes at a point in the world: the coverage over it, rounded off at the
// slab's bottom and top, its edges worn away by the finer noise.
float GetCloudExtinction(Air air, float3 world, float time)
{
    float height = (world.y - air.cloudHeight) / CloudThickness;
    if (height <= 0.0 || height >= 1.0)
        return 0.0;

    float coverage = GetCloudCoverage(air, world.xz, time) * saturate(height * 4.0) * saturate((1.0 - height) * 3.0);
    if (coverage <= 0.0)
        return 0.0;

    // The finer noise, carried by the wind, in [0, 1]: a value a whole cell, blended between cells.
    float3 at = (world - float3(air.wind.x, 0.0, air.wind.y) * time) / DetailScale;
    int3 cell = int3(floor(at));
    float3 f = smoothstep(0.0, 1.0, frac(at));
    float corners[8];
    for (uint i = 0u; i < 8u; i++)
    {
        int3 corner = cell + int3(i & 1u, (i >> 1u) & 1u, i >> 2u);
        corners[i] = ToUnitFloat(GetHash(uint3(corner)).x);
    }

    float4 alongX = lerp(
        float4(corners[0], corners[2], corners[4], corners[6]),
        float4(corners[1], corners[3], corners[5], corners[7]),
        f.x
    );
    float2 alongY = lerp(alongX.xz, alongX.yw, f.y);
    float detail = lerp(alongY.x, alongY.y, f.z);
    return CloudExtinction * saturate(coverage * 1.3 - detail * 0.3);
}

// A thread a block of texels: the light the clouds send towards the camera along its texel's direction this
// frame, in rgb, and how much of the sky behind they hide, in alpha.
[shader("compute")]
[numthreads(8, 8, 1)]
void DrawClouds(uint3 thread : SV_DispatchThreadID)
{
    PassHeader header = GetPassHeader();
    StorageRef picture = { header.y, 0u };
    uint2 size = picture.GetSize();
    FrameConstants frame = GetFrame();
    uint block = header.z;
    uint turn = frame.frameIndex % (block * block);
    uint2 texel = thread.xy * block + uint2(turn % block, turn / block);
    if (any(texel >= size))
        return;

    Air air = GetAir();
    Lights lights = GetLights();
    float3 direction = FromCloudUv(GetTexelCenterUv(texel, size));
    float3 camera = frame.cameraPosition.xyz;
    float base = air.cloudHeight;
    float top = base + CloudThickness;
    float4 seen = float4(0.0);
    if (direction.y > 0.01 && camera.y < top)
    {
        // Where the direction enters and leaves the slab, from inside it where the camera is.
        float enter = max((base - camera.y) / direction.y, 0.0);
        float leave = min((top - camera.y) / direction.y, enter + CrossingReach);
        float step = (leave - enter) / float(Steps);
        float start = enter + step * GetRandom(texel, frame.frameIndex, 0u).x;
        float3 toSun = lights.sunDirection.xyz;
        float cosine = dot(direction, toSun);
        float phase = lerp(GetAirPhase(0.8, cosine), GetAirPhase(-0.3, cosine), 0.3);
        float3 sun = lights.sunIlluminance.rgb * phase;
        float3 sky = GetOpenSkyRadiance(lights);
        float3 lit = float3(0.0);
        float through = 1.0;
        for (uint i = 0u; i < Steps && through > 0.01; i++)
        {
            float3 at = camera + direction * (start + step * float(i));
            float extinction = GetCloudExtinction(air, at, frame.timeSec);
            if (extinction <= 0.0)
                continue;

            // The sun's light through the cloud towards it, brightened where the cloud is thick, as a cloud's dark
            // edges and bright folds show (Beer-powder).
            float depth = 0.0;
            for (uint j = 1u; j <= SunSteps; j++)
            {
                depth += GetCloudExtinction(air, at + toSun * SunStep * float(j), frame.timeSec) * SunStep;
            }

            float sunThrough = exp(-depth) * lerp(1.0, 2.0 * (1.0 - exp(-2.0 * depth)), 0.5);
            float height = saturate((at.y - base) / CloudThickness);
            float3 scattered = sun * sunThrough + sky * (0.5 + 0.5 * height);

            // Each step's light as the cloud through it lets it out (Hillaire, "Physically Based Sky, Atmosphere and
            // Cloud Rendering in Frostbite", 2016).
            float stepThrough = exp(-extinction * step);
            lit += through * scattered * (1.0 - stepThrough);
            through *= stepThrough;
        }

        // Clouds near the horizon fade into the sky.
        float fade = saturate(1.0 - enter / CloudReach);
        seen = float4(lit, 1.0 - through) * fade;
    }

    if (block > 1u)
        seen = lerp(picture.Load(texel), seen, 0.5);

    picture.Store(texel, seen);
}
