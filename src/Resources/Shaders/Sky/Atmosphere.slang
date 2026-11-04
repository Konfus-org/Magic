// The Earth's air: Rayleigh and Mie scattering and ozone absorption, integrated into three lookup tables ahead of
// the frame, from which the sky, the sun's colour and the sky's light on the ground all come (Hillaire, "A Scalable
// and Production Ready Sky and Atmosphere Rendering Technique", 2020). Distances are in kilometres.
module "Sky/Atmosphere";

import Core.Heap;
import Core.Math;
import Sky.ISky;

public static const float GroundRadius = 6360.0;
public static const float TopRadius = 6460.0;
static const float3 RayleighScattering = float3(5.802, 13.558, 33.1) * 0.001;
static const float RayleighHeight = 8.0;
static const float MieScattering = 3.996 * 0.001;
static const float MieAbsorption = 4.4 * 0.001;
static const float MieHeight = 1.2;
static const float3 OzoneAbsorption = float3(0.650, 1.881, 0.085) * 0.001;
static const float OzoneCenter = 25.0;
static const float OzoneWidth = 15.0;
static const float MieAnisotropy = 0.8;
static const float3 GroundAlbedo = float3(0.3);
static const float SunCosRadius = 0.99998869;

public static const uint2 TransmittanceLutSize = uint2(256u, 64u);
public static const uint2 MultiScatteringLutSize = uint2(32u, 32u);
public static const uint2 SkyViewLutSize = uint2(192u, 108u);

// What the air does at a height: what it scatters, what it takes out.
struct Medium
{
    float3 scattering;
    float3 extinction;
    float3 rayleigh;
    float mie;
}

Medium SampleMedium(float radius)
{
    float height = max(radius - GroundRadius, 0.0);
    float rayleighDensity = exp(-height / RayleighHeight);
    float mieDensity = exp(-height / MieHeight);
    float ozoneDensity = saturate(1.0 - abs(height - OzoneCenter) / OzoneWidth);
    Medium medium;
    medium.rayleigh = RayleighScattering * rayleighDensity;
    medium.mie = MieScattering * mieDensity;
    medium.scattering = medium.rayleigh + medium.mie;
    medium.extinction =
        medium.rayleigh + (MieScattering + MieAbsorption) * mieDensity + OzoneAbsorption * ozoneDensity;
    return medium;
}

float PhaseRayleigh(float cosine) { return 3.0 / (16.0 * Pi) * (1.0 + cosine * cosine); }

float PhaseMie(float cosine)
{
    float g = MieAnisotropy;
    float k = 3.0 / (8.0 * Pi) * (1.0 - g * g) / (2.0 + g * g);
    return k * (1.0 + cosine * cosine) / pow(1.0 + g * g - 2.0 * g * cosine, 1.5);
}

// Where a ray from a point at radius r, at cosine mu to the zenith, leaves a sphere of radius R; negative for never.
float DistanceToSphere(float r, float mu, float R)
{
    float discriminant = r * r * (mu * mu - 1.0) + R * R;
    if (discriminant < 0.0)
        return -1.0;

    float root = sqrt(discriminant);
    float near = -r * mu - root;
    float far = -r * mu + root;
    return near > 0.0 ? near : far;
}

// How far a ray goes through the air before it leaves the top or hits the ground.
float DistanceThroughAir(float r, float mu)
{
    float ground = DistanceToSphere(r, mu, GroundRadius);
    float top = DistanceToSphere(r, mu, TopRadius);
    return ground > 0.0 ? ground : top;
}

bool HitsGround(float r, float mu) { return DistanceToSphere(r, mu, GroundRadius) > 0.0; }

// The transmittance table's coordinates from a radius and a cosine, spread so the horizon is sharp (Bruneton and
// Neyret, "Precomputed Atmospheric Scattering", 2008).
public float2 TransmittanceUv(float r, float mu)
{
    float h = sqrt(TopRadius * TopRadius - GroundRadius * GroundRadius);
    float rho = sqrt(max(r * r - GroundRadius * GroundRadius, 0.0));
    float d = DistanceToSphere(r, mu, TopRadius);
    float dMin = TopRadius - r;
    float dMax = rho + h;
    return float2((d - dMin) / max(dMax - dMin, Epsilon), rho / h);
}

public void TransmittanceParams(float2 uv, out float r, out float mu)
{
    float h = sqrt(TopRadius * TopRadius - GroundRadius * GroundRadius);
    float rho = h * uv.y;
    r = sqrt(rho * rho + GroundRadius * GroundRadius);
    float dMin = TopRadius - r;
    float dMax = rho + h;
    float d = dMin + uv.x * (dMax - dMin);
    mu = d == 0.0 ? 1.0 : (h * h - rho * rho - d * d) / (2.0 * r * d);
    mu = clamp(mu, -1.0, 1.0);
}

// What is left of light that crossed the air from a point to its edge, integrated here: the table's entry.
public float3 ComputeTransmittance(float r, float mu)
{
    float distance = DistanceThroughAir(r, mu);
    const uint steps = 40u;
    float3 optical = float3(0.0);
    for (uint i = 0u; i < steps; i++)
    {
        float t = (float(i) + 0.5) / float(steps) * distance;
        float radius = sqrt(r * r + t * t + 2.0 * r * mu * t);
        optical += SampleMedium(radius).extinction * (distance / float(steps));
    }

    return exp(-optical);
}

public float3 LookupTransmittance(TextureRef lut, SamplerRef sampler, float r, float mu)
{
    return lut.SampleLevel(sampler, TransmittanceUv(r, mu), 0.0).rgb;
}

// Light from the sun scattered along a ray, and how much of it would go on to scatter again: for the
// multi-scattering table and the sky itself.
struct Scattered
{
    float3 luminance;
    float3 multiScatter;
    float3 transmittance;
}

Scattered Integrate(
    TextureRef transmittanceLut,
    TextureRef multiScatteringLut,
    SamplerRef sampler,
    float3 origin,
    float3 direction,
    float3 sunDirection,
    uint steps,
    bool usesMultiScattering
)
{
    float r = length(origin);
    float3 zenith = origin / r;
    float mu = dot(zenith, direction);
    float distance = DistanceThroughAir(r, mu);
    float cosine = dot(direction, sunDirection);
    float rayleighPhase = PhaseRayleigh(cosine);
    float miePhase = PhaseMie(cosine);
    Scattered result;
    result.luminance = float3(0.0);
    result.multiScatter = float3(0.0);
    result.transmittance = float3(1.0);
    float previous = 0.0;
    for (uint i = 0u; i < steps; i++)
    {
        float t = (float(i) + 0.3) / float(steps) * distance;
        float dt = t - previous;
        previous = t;
        float3 point = origin + direction * t;
        float radius = length(point);
        Medium medium = SampleMedium(radius);
        float3 stepTransmittance = exp(-medium.extinction * dt);
        float sunMu = dot(point / radius, sunDirection);
        float3 sunTransmittance = HitsGround(radius, sunMu)
            ? float3(0.0)
            : LookupTransmittance(transmittanceLut, sampler, radius, sunMu);
        float3 multi = float3(0.0);
        if (usesMultiScattering)
        {
            float2 uv = saturate(float2(sunMu * 0.5 + 0.5, (radius - GroundRadius) / (TopRadius - GroundRadius)));
            multi = multiScatteringLut.SampleLevel(sampler, uv, 0.0).rgb;
        }

        float3 phased = medium.rayleigh * rayleighPhase + medium.mie * miePhase;
        float3 scattered = phased * sunTransmittance + medium.scattering * multi;
        float3 integral = (scattered - scattered * stepTransmittance) / max(medium.extinction, Epsilon);
        result.luminance += result.transmittance * integral;
        float3 multiIntegral = (medium.scattering - medium.scattering * stepTransmittance) / max(medium.extinction, Epsilon);
        result.multiScatter += result.transmittance * multiIntegral;
        result.transmittance *= stepTransmittance;
    }

    return result;
}

// The multi-scattering table's entry for a height and a sun: what every direction scatters of the sun's light and
// of itself, summed as a series.
public float3 ComputeMultiScattering(TextureRef transmittanceLut, SamplerRef sampler, float2 uv)
{
    float sunMu = uv.x * 2.0 - 1.0;
    float radius = GroundRadius + uv.y * (TopRadius - GroundRadius);
    float3 origin = float3(0.0, radius, 0.0);
    float3 sunDirection = float3(sqrt(saturate(1.0 - sunMu * sunMu)), sunMu, 0.0);
    const uint directions = 64u;
    float3 luminance = float3(0.0);
    float3 multi = float3(0.0);
    for (uint i = 0u; i < directions; i++)
    {
        // Directions spread over the sphere by a spiral.
        float elevation = 1.0 - 2.0 * (float(i) + 0.5) / float(directions);
        float ring = sqrt(saturate(1.0 - elevation * elevation));
        float angle = float(i) * 2.399963;
        float3 direction = float3(ring * cos(angle), elevation, ring * sin(angle));
        Scattered scattered = Integrate(transmittanceLut, transmittanceLut, sampler, origin, direction, sunDirection, 20u, false);
        float mu = dot(origin / radius, direction);
        if (HitsGround(radius, mu))
        {
            float3 ground = origin + direction * DistanceToSphere(radius, mu, GroundRadius);
            float groundMu = dot(ground / GroundRadius, sunDirection);
            float3 sunAtGround = LookupTransmittance(transmittanceLut, sampler, GroundRadius, groundMu);
            scattered.luminance += scattered.transmittance * GroundAlbedo * InvPi * saturate(groundMu) * sunAtGround;
        }

        luminance += scattered.luminance;
        multi += scattered.multiScatter;
    }

    luminance /= float(directions);
    multi /= float(directions);
    return luminance / max(1.0 - multi, Epsilon);
}

// The sky-view table's direction for its coordinates: azimuth across, and elevation down with the horizon in the
// middle and the sky pressed towards it, where the air changes fastest.
public float3 SkyViewDirection(float2 uv, float altitude)
{
    float r = GroundRadius + max(altitude, 0.001);
    float horizon = -acos(GroundRadius / r);
    float azimuth = (uv.x * 2.0 - 1.0) * Pi;
    float elevation;
    if (uv.y < 0.5)
    {
        float t = 1.0 - uv.y * 2.0;
        elevation = horizon + (HalfPi - horizon) * t * t;
    }
    else
    {
        float t = uv.y * 2.0 - 1.0;
        elevation = horizon - (HalfPi + horizon) * t * t;
    }

    float cosElevation = cos(elevation);
    return float3(cosElevation * sin(azimuth), sin(elevation), -cosElevation * cos(azimuth));
}

public float2 SkyViewUv(float3 direction, float altitude)
{
    float r = GroundRadius + max(altitude, 0.001);
    float horizon = -acos(GroundRadius / r);
    float elevation = asin(clamp(direction.y, -1.0, 1.0));
    float azimuth = atan2(direction.x, -direction.z);
    float v;
    if (elevation > horizon)
        v = 0.5 - 0.5 * sqrt(saturate((elevation - horizon) / (HalfPi - horizon)));
    else
        v = 0.5 + 0.5 * sqrt(saturate((horizon - elevation) / (HalfPi + horizon)));

    return float2(azimuth / (2.0 * Pi) + 0.5, v);
}

// The sky-view table's entry: the sky along a direction, for a unit of the sun's illuminance, with the sun's light
// turned by the azimuth so the table is centred on the sun.
public float3 ComputeSkyView(
    TextureRef transmittanceLut,
    TextureRef multiScatteringLut,
    SamplerRef sampler,
    float3 direction,
    float3 sunDirection,
    float altitude
)
{
    float3 origin = float3(0.0, GroundRadius + max(altitude, 0.001), 0.0);
    Scattered scattered = Integrate(transmittanceLut, multiScatteringLut, sampler, origin, direction, sunDirection, 32u, true);
    float r = length(origin);
    float mu = dot(origin / r, direction);
    if (HitsGround(r, mu))
    {
        // The ground under the sky, lit by the sun through the air, where the view reaches it.
        float3 ground = origin + direction * DistanceToSphere(r, mu, GroundRadius);
        float groundMu = dot(ground / GroundRadius, sunDirection);
        float3 sunAtGround = LookupTransmittance(transmittanceLut, sampler, GroundRadius, groundMu);
        scattered.luminance += scattered.transmittance * GroundAlbedo * InvPi * saturate(groundMu) * sunAtGround;
    }

    return scattered.luminance;
}

public struct Atmosphere : ISky
{
    public static float3 Evaluate(SkyInputs inputs)
    {
        TextureRef skyView = { inputs.skyViewLut };
        TextureRef transmittance = { inputs.transmittanceLut };
        SamplerRef sampler = { inputs.sampler };
        float3 sky = skyView.SampleLevel(sampler, SkyViewUv(inputs.viewDirection, inputs.altitude * 0.001), 0.0).rgb;
        float3 color = sky * inputs.sunIlluminance;
        float r = GroundRadius + max(inputs.altitude * 0.001, 0.001);
        if (inputs.withSun && dot(inputs.viewDirection, inputs.sunDirection) > SunCosRadius && !HitsGround(r, inputs.viewDirection.y))
        {
            // The disc: the sun's illuminance over its solid angle, dimmed by the air in the way.
            float3 through = LookupTransmittance(transmittance, sampler, r, inputs.viewDirection.y);
            color += inputs.sunIlluminance * through / (Pi * (1.0 - SunCosRadius * SunCosRadius)) * 0.05;
        }

        return color;
    }

    public static float3 GetSunIlluminance(SkyInputs inputs)
    {
        TextureRef transmittance = { inputs.transmittanceLut };
        SamplerRef sampler = { inputs.sampler };
        float r = GroundRadius + max(inputs.altitude * 0.001, 0.001);
        return inputs.sunIlluminance * LookupTransmittance(transmittance, sampler, r, inputs.sunDirection.y);
    }

    public static float3 GetAmbient(SkyInputs inputs, float3 normal)
    {
        // The sky over the surface's hemisphere, sampled where it is brightest and around.
        TextureRef skyView = { inputs.skyViewLut };
        SamplerRef sampler = { inputs.sampler };
        float altitude = inputs.altitude * 0.001;
        float3 up = float3(0.0, 1.0, 0.0);
        float3 side = NormalizeSafe(cross(normal, up), float3(1.0, 0.0, 0.0));
        float3 forward = cross(side, normal);
        float3 sum = float3(0.0);
        float weight = 0.0;
        for (uint i = 0u; i < 5u; i++)
        {
            float3 direction = normal;
            if (i == 1u)
                direction = normalize(normal + side);
            else if (i == 2u)
                direction = normalize(normal - side);
            else if (i == 3u)
                direction = normalize(normal + forward);
            else if (i == 4u)
                direction = normalize(normal - forward);

            float cosine = i == 0u ? 1.0 : 0.7071;
            if (direction.y < 0.0)
                continue;

            sum += skyView.SampleLevel(sampler, SkyViewUv(direction, altitude), 0.0).rgb * cosine;
            weight += cosine;
        }

        float3 sky = weight > 0.0 ? sum / weight : float3(0.0);
        return sky * inputs.sunIlluminance * Pi * saturate(0.5 + 0.5 * normal.y);
    }
}
