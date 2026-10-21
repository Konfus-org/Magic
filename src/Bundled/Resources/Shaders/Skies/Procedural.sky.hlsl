// The engine's procedural sky: air over a round planet lit by the world's sun, a layer of clouds the world's weather
// moves and colours, and the stars once the sun is down. Nothing in it is painted: the blue of noon, the orange of a
// low sun and the red of its disc come from the light crossing more air, so the sky follows the sun wherever and
// whatever colour it is, and the world it lights follows the sky.
//
// - The air scatters the sun's light toward the camera once along the view ray (Nishita et al. 1993, "Display of the
//   Earth Taking into Account Atmospheric Scattering"): molecules by Rayleigh's law, dust by a Henyey-Greenstein lobe,
//   with Earth's coefficients as Hillaire 2020 lists them ("A Scalable and Production Ready Sky and Atmosphere
//   Rendering Technique"). Light scattered more than once is close to the same every way, as that paper finds, so it
//   is added as an even share of what each point scatters rather than traced. Each step's light is integrated across
//   the step as its own transmittance falls (Hillaire 2015, "Physically Based and Unified Volumetric Rendering in
//   Frostbite"), so few steps do not brighten or darken it.
// - The clouds are one layer at a height, where the view ray meets it, their cover improved gradient noise (Perlin
//   2002) over a few octaves, cut at the weather's coverage and carried by its wind. They are lit by the sun through
//   the air above them, so a low sun reddens them as it does the sky, through the cloud toward the sun (Beer's law),
//   darker on the side away from it (the powder term of Schneider 2015, "The Real-time Volumetric Cloudscapes of
//   Horizon Zero Dawn"), brighter looking toward it (two Henyey-Greenstein lobes), and by the sky from around them in
//   their shade. The weather's two colours tint the lit and the shaded light, so pink and purple clouds are weather.
// - The sun's disc is as wide as the material says, darker toward its rim (limb darkening), seen through the air and
//   the clouds; its light is the sun's, so a wider disc is dimmer, not brighter.

#include "Include/Noise.hlsli"
#include "Include/Sky.hlsli"

struct MaterialParams
{
    Color groundColor = Color(0.3, 0.28, 0.25, 1.0);      // the ground seen below the horizon, its albedo
    Color nightSky = Color(0.0004, 0.0006, 0.0012, 1.0);  // nits of the sky with the sun down, moonless
    float cloudHeight = 1500.0;      // metres above the world's origin the clouds lie at
    float cloudScale = 3000.0;       // metres across a cloud, about
    float cloudDetail = 0.5;         // how much finer noise breaks up the clouds' edges, 0 to 1
    float cloudSoftness = 0.3;       // how gradually a cloud's edge fades, 0 hard to 1 soft
    float haze = 1.0;                // dust in the air, as a multiple of a clear day's: more whitens the sky
    float hazeDirection = 0.8;       // how much the dust scatters forward, 0 to below 1: nearer 1, a tighter glow
    float sunSize = 1.0;             // the disc's radius as a multiple of the sun's own
    float multipleScattering = 1.0;  // how much of the light scattered more than once is added, 1 as much as there is
    float stars = 1.0;               // how bright the stars are, 0 for none
};

// The planet and its air, in metres.
static const float PlanetRadius = 6360000.0;
static const float AtmosphereRadius = 6460000.0;

// Scattering and extinction per metre at the ground, and the heights over which each thins by e.
static const float3 RayleighScattering = float3(5.802e-6, 13.558e-6, 33.1e-6);
static const float RayleighHeight = 8000.0;
static const float MieScattering = 3.996e-6;
static const float MieExtinction = 4.44e-6;
static const float MieHeight = 1200.0;

// Steps along the view ray, in view and into the capture, and toward the sun from each.
static const uint ViewSteps = 16u;
static const uint CaptureSteps = 8u;
static const uint LightSteps = 4u;

// Octaves of the clouds' noise in view and into the capture, and toward the sun.
static const uint CloudOctaves = 5u;
static const uint CaptureCloudOctaves = 3u;
static const uint ShadowOctaves = 2u;

// How the clouds' optical depth ranges from the thinnest weather to the thickest, through the thickness of a cloud.
static const float ThinCloud = 2.0;
static const float ThickCloud = 24.0;

// Metres toward the sun the clouds' own shade is looked up at, and the share of the cloud's depth it sees.
static const float CloudShadowStep = 300.0;
static const float CloudShadowShare = 0.35;

// Metres past which the clouds fade out, as the layer thins to a line at the horizon.
static const float CloudFade = 60000.0;

// How fast the clouds change shape with no wind, in noise cells a second.
static const float CloudChange = 0.002;

// The sky's light from around a cloud, per lux of the sun reaching it: a clear sky's is around 0.03 to 0.08.
static const float AmbientPerLux = 0.06;

// The light a thick cloud sends back of what reaches it, over pi.
static const float CloudAlbedo = 0.9;

// The two lobes clouds scatter by, forward and back, and the share of the forward one (Hillaire 2016).
static const float CloudForward = 0.6;
static const float CloudBack = -0.25;
static const float CloudForwardShare = 0.7;

// The sun's limb darkening, a linear law: its rim is this much darker than its middle.
static const float LimbDarkening = 0.6;

// Stars: cells of the sky a radian across, the share of cells with a star, how fast one fades off its centre, and the
// brightest's nits.
static const float StarCells = 400.0;
static const float StarShare = 0.004;
static const float StarSharpness = 2.0e6;
static const float StarNits = 0.05;

// Where a ray from a place relative to the planet's centre meets a sphere about it: the near and far distances, both
// negative when it misses. The square of the place's distance less the radius's is factored, as their difference
// would lose everything a float holds at a planet's size.
float2 SphereHits(float3 origin, float3 direction, float radius)
{
    float fromCentre = length(origin);
    float along = dot(origin, direction);
    float discriminant = along * along - (fromCentre - radius) * (fromCentre + radius);
    if (discriminant < 0.0)
    {
        return float2(-1.0, -1.0);
    }

    float root = sqrt(discriminant);
    return float2(-along - root, -along + root);
}

// The air's density at a height, of molecules then dust, as a share of the ground's.
float2 AirDensity(float height)
{
    return exp(-max(height, 0.0) / float2(RayleighHeight, MieHeight));
}

float RayleighPhase(float cosAngle)
{
    return 3.0 / (16.0 * Pi) * (1.0 + cosAngle * cosAngle);
}

float HenyeyGreenstein(float cosAngle, float g)
{
    float g2 = g * g;
    return (1.0 - g2) / (4.0 * Pi * pow(max(1.0 + g2 - 2.0 * g * cosAngle, 1e-4), 1.5));
}

// How much of the sun's light reaches a place relative to the planet's centre through the air, by colour; none where
// the planet is in the way.
float3 SunTransmittance(float3 origin, float3 towardSun, float haze)
{
    if (SphereHits(origin, towardSun, PlanetRadius).x > 0.0)
    {
        return float3(0.0, 0.0, 0.0);
    }

    float span = max(SphereHits(origin, towardSun, AtmosphereRadius).y, 0.0) / (float)LightSteps;
    float2 depth = float2(0.0, 0.0);
    [loop] for (uint i = 0u; i < LightSteps; i++)
    {
        float3 at = origin + towardSun * (((float)i + 0.5) * span);
        depth += AirDensity(length(at) - PlanetRadius) * span;
    }

    return exp(-(RayleighScattering * depth.x + MieExtinction * haze * depth.y));
}

// How much of the sky clouds cover at a place of the noise's space, 0 to 1, cut at the weather's coverage.
float CloudCover(float3 place, float coverage, float detail, float softness, uint octaves)
{
    float sum = 0.0;
    float total = 0.0;
    float amplitude = 0.5;
    [loop] for (uint octave = 0u; octave < octaves; octave++)
    {
        float weight = octave >= 2u ? amplitude * 2.0 * saturate(detail) : amplitude;
        sum += GradientNoise(place) * weight;
        total += weight;
        amplitude *= 0.5;
        place = place * 2.03 + float3(17.1, 4.7, 9.3);
    }

    float noise = saturate(sum / max(total, 1e-4) * 0.8 + 0.5);
    float edge = max(softness, 0.01) * 0.5;
    return saturate((noise - (1.0 - coverage)) / edge);
}

// A star's light along a direction: a few cells of the sky hold one, somewhere in the cell, of its own brightness and
// tint.
float3 StarLight(float3 direction)
{
    float3 cell = floor(direction * StarCells);
    uint hash = LatticeHash(int3(cell));
    float chance = HashToUnit(hash);
    if (chance < 1.0 - StarShare)
    {
        return float3(0.0, 0.0, 0.0);
    }

    float3 jitter = float3(HashToUnit(PcgHash(hash)), HashToUnit(PcgHash(hash + 1u)), HashToUnit(PcgHash(hash + 2u)));
    float3 star = normalize((cell + jitter) / StarCells);
    float brightness = exp(-(1.0 - dot(direction, star)) * StarSharpness) * (chance - (1.0 - StarShare)) / StarShare;
    float3 tint = lerp(float3(1.0, 0.8, 0.6), float3(0.7, 0.8, 1.0), HashToUnit(PcgHash(hash + 3u)));
    return tint * (brightness * StarNits);
}

SkyLight EvaluateSky(SkyInputs input, MaterialParams material)
{
    float3 direction = input.direction;
    float3 towardSun = -input.sunDirection;
    float3 sunLux = input.sunIlluminance;
    float haze = max(material.haze, 0.0);
    float cosSun = dot(direction, towardSun);
    float3 origin = float3(0.0, PlanetRadius + max(input.cameraPosition.y, 0.0) + 1.0, 0.0);

    // Where the view ray is in the air: from the camera, or where it comes into the air from above, to where it leaves
    // it or meets the ground.
    float2 air = SphereHits(origin, direction, AtmosphereRadius);
    float2 ground = SphereHits(origin, direction, PlanetRadius);
    bool isGround = ground.x > 0.0;
    float rayStart = max(air.x, 0.0);
    float rayEnd = isGround ? ground.x : max(air.y, 0.0);

    // Where it meets the clouds' layer: the far side of it from beneath, the near side from above.
    float2 layer = SphereHits(origin, direction, PlanetRadius + material.cloudHeight);
    float cloudDistance = layer.x > 0.0 ? layer.x : layer.y;
    bool hasClouds = input.coverage > 0.0 && cloudDistance > 0.0 && cloudDistance < rayEnd;
    float splitAt = hasClouds ? cloudDistance : rayEnd;

    // The air's light in front of the clouds and past them, each step's integrated across it, steps closer together
    // near the camera, where the air is thickest along most rays.
    uint steps = input.isCapture ? CaptureSteps : ViewSteps;
    float rayleighPhase = RayleighPhase(cosSun);
    float miePhase = HenyeyGreenstein(cosSun, clamp(material.hazeDirection, 0.0, 0.99));
    float evenPhase = max(material.multipleScattering, 0.0) / (4.0 * Pi);
    float3 front = float3(0.0, 0.0, 0.0);
    float3 past = float3(0.0, 0.0, 0.0);
    float3 through = float3(1.0, 1.0, 1.0);
    float3 throughFront = float3(1.0, 1.0, 1.0);
    float span = rayEnd - rayStart;
    [loop] for (uint i = 0u; i < steps; i++)
    {
        float stepStart = rayStart + span * Pow2((float)i / (float)steps);
        float stepEnd = rayStart + span * Pow2((float)(i + 1u) / (float)steps);
        float at = 0.5 * (stepStart + stepEnd);
        float3 place = origin + direction * at;
        float2 density = AirDensity(length(place) - PlanetRadius);
        float3 rayleigh = RayleighScattering * density.x;
        float3 mie = float3(1.0, 1.0, 1.0) * (MieScattering * haze * density.y);
        float3 extinction = rayleigh + float3(1.0, 1.0, 1.0) * (MieExtinction * haze * density.y);
        float3 sunReached = SunTransmittance(place, towardSun, haze);
        float3 scattered = sunReached * (rayleigh * rayleighPhase + mie * miePhase + (rayleigh + mie) * evenPhase);
        float3 stepThrough = exp(-extinction * (stepEnd - stepStart));
        float3 gathered = through * scattered * (1.0 - stepThrough) / max(extinction, 1e-12);
        if (at < splitAt)
        {
            front += gathered;
        }
        else
        {
            past += gathered;
        }

        through *= stepThrough;
        if (at < splitAt)
        {
            throughFront = through;
        }
    }

    // What is past the clouds besides the air's light: the ground, or the night sky and its stars.
    float3 beyond = past * sunLux;
    if (isGround)
    {
        float3 landed = origin + direction * rayEnd;
        float3 up = normalize(landed);
        float3 sunOnGround = SunTransmittance(landed + up, towardSun, haze) * sunLux;
        beyond += through * material.groundColor.rgb * sunOnGround * (saturate(dot(up, towardSun)) * InvPi);
    }
    else
    {
        float day = saturate(towardSun.y * 10.0 + 0.5);
        beyond += through * (material.nightSky.rgb + StarLight(direction) * (max(material.stars, 0.0) * (1.0 - day)));
    }

    // The clouds: their cover where the ray meets them, carried by the wind and changing slowly, faded out toward the
    // horizon; lit by the sun through the air above them and through the cloud toward it, and by the sky around them.
    float cover = 0.0;
    float3 cloudLight = float3(0.0, 0.0, 0.0);
    if (hasClouds)
    {
        float scale = max(material.cloudScale, 1.0);
        float2 where = input.cameraPosition.xz + direction.xz * cloudDistance - input.wind.xz * input.time;
        float3 place = float3(where / scale, input.time * CloudChange);
        uint octaves = input.isCapture ? CaptureCloudOctaves : CloudOctaves;
        float thickness = lerp(ThinCloud, ThickCloud, saturate(input.density));
        float depth = CloudCover(place, input.coverage, material.cloudDetail, material.cloudSoftness, octaves) * thickness;
        cover = (1.0 - exp(-depth)) * saturate(1.0 - cloudDistance / CloudFade);

        float3 towardSunPlace = float3(towardSun.xz * (CloudShadowStep / scale), 0.0);
        float shade = CloudCover(place + towardSunPlace, input.coverage, material.cloudDetail, material.cloudSoftness, ShadowOctaves) * thickness;
        float beer = exp(-shade * CloudShadowShare);
        float powder = lerp(1.0, 1.0 - exp(-2.0 * depth), saturate(0.5 - 0.5 * cosSun));
        float phase = 4.0 * Pi * lerp(HenyeyGreenstein(cosSun, CloudBack), HenyeyGreenstein(cosSun, CloudForward), CloudForwardShare);
        float3 sunOnCloud = SunTransmittance(origin + direction * cloudDistance, towardSun, haze) * sunLux;
        float3 sunlit = input.cloudColor * sunOnCloud * (beer * powder * phase * CloudAlbedo * InvPi);
        float3 skylit = input.cloudShadow * (sunOnCloud * AmbientPerLux + material.nightSky.rgb);
        cloudLight = sunlit + skylit;
    }

    SkyLight light;
    light.nits = front * sunLux + throughFront * cloudLight * cover + beyond * (1.0 - cover);

    // The sun's disc, darker toward its rim, through the air and the clouds in front of it.
    light.emissive = float3(0.0, 0.0, 0.0);
    light.sun = float3(0.0, 0.0, 0.0);
    float radius = input.sunAngularRadius * max(material.sunSize, 0.01);
    float off = sqrt(max(2.0 * (1.0 - cosSun), 0.0)) / radius;
    if (!isGround && off < 1.0)
    {
        float limb = 1.0 - LimbDarkening * (1.0 - sqrt(1.0 - off * off));
        light.sun = sunLux * through * (1.0 - cover) * (limb / ((1.0 - LimbDarkening / 3.0) * Pi * radius * radius));
    }

    return light;
}
