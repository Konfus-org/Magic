// The engine's procedural sky: air over a round planet lit by the world's sun and moon, clouds the world's weather moves
// and colours, and the stars once the sun is down. Nothing in it is painted: the blue of noon, the orange of a low sun
// and the red of its disc come from the light crossing more air, so the sky follows the sun wherever and whatever colour
// it is, and the world it lights follows the sky.
//
// - The air scatters the sun's and the moon's light toward the camera once along the view ray (Nishita et al. 1993,
//   "Display of the Earth Taking into Account Atmospheric Scattering"): molecules by Rayleigh's law, dust by a
//   Henyey-Greenstein lobe, with Earth's coefficients as Hillaire 2020 lists them ("A Scalable and Production Ready Sky
//   and Atmosphere Rendering Technique"). Light scattered more than once is close to the same every way, as that paper
//   finds, so it is added as an even share of what each point scatters rather than traced. Each step's light is
//   integrated across the step as its own transmittance falls (Hillaire 2015, "Physically Based and Unified Volumetric
//   Rendering in Frostbite"), so few steps do not brighten or darken it. The moon's is only worked out while it is not
//   lost beside the sun's, so it costs nothing by day. The same air lies between the camera and the world, so a far
//   mountain fades into the light the sky shows past it.
// - The clouds fill a layer of the air between two heights, marched through along the view (Schneider 2015, "The
//   Real-time Volumetric Cloudscapes of Horizon Zero Dawn"; Hillaire 2016, "Physically Based Sky, Atmosphere and Cloud
//   Rendering in Frostbite"). How much cloud a place holds is the weather's cover over it, improved gradient noise
//   (Perlin 2002) cut at its coverage and carried by its wind, shaped by the place's height in the layer, a rounded base
//   and a top that rises where the cover is thicker, so thin cover makes flat clouds and thick cover towers; and eaten
//   at its thin edges by billowing noise. Each step is lit by the sun through the air above the clouds and through the
//   cloud toward it, a few taps along its way, with the light scattered more than once let further in (Wrenninge 2013,
//   "Oz: The Great and Volumetric") and what diffuses through a thick cloud added (Bohren 1987), so an overcast is
//   dim beneath but not black, darker on the side away from it (the powder term of Schneider 2015), brighter
//   looking toward it (two Henyey-Greenstein lobes), and by the sky from around them. The steps start a dither apart
//   that moves each frame, which the view's temporal upscale blends into many more. The weather's two colours tint the
//   lit and the shaded light, so pink and purple clouds are weather. The moon lights them as the sun does, while its
//   light is not lost beside the sun's. What the air and the clouds let through of the sun or the moon lights the
//   world, the air's transmittance and taps of the same clouds along the light's way to each place, so the world
//   reddens and dims as the sun sets, their shadows cross the ground with the wind, and an overcast sky leaves the world
//   no sunlight.
// - The sun's disc is as wide as its light says, darker toward its rim and redder there, as blue light darkens most
//   (limb darkening), seen through the air and the clouds; its light is the sun's, so a wider disc is dimmer, not
//   brighter.
// - The moon is a sphere lit from the sun's side, so its phase follows from where the sun is, its face lit as the
//   moon's dust scatters (Lommel-Seeliger), so a full moon looks flat, as the real one does, its dark side faintly lit
//   by the Earth. Its face is the material's image of it, or one of maria and craters made from noise, as bright as the
//   material says for its light, so it can show its face by night, where a real moon's would be a white disc beside a
//   ground exposed for its light. Its light adds to the sky's in front of it, so by day it shows only as much as it
//   stands out against the sky's own brightness.
// - The stars: a share of the sky's cells holds one each, most of them faint and a few bright, tinted between two
//   colours, twinkling more near the horizon, where their light crosses the most air.

#include "Include/Color.hlsli"
#include "Include/Noise.hlsli"
#include "Include/Sky.hlsli"

struct MaterialParams
{
    Color groundColor = Color(0.3, 0.28, 0.25, 1.0);      // the ground seen below the horizon, its albedo
    Color nightSky = Color(0.0004, 0.0006, 0.0012, 1.0);  // nits of the sky with the sun down, moonless
    float cloudHeight = 1500.0;      // metres above the world's origin the clouds' bases lie at
    float cloudThickness = 1200.0;   // metres from the clouds' bases to the highest of their tops
    float cloudScale = 3000.0;       // metres across a cloud, about
    float cloudDetail = 0.5;         // how much billowing noise eats into the clouds' edges, 0 to 1
    float cloudSoftness = 0.3;       // how gradually the cover fades at a cloud's edge, 0 hard to 1 soft
    float haze = 1.0;                // dust in the air, as a multiple of a clear day's: more whitens the sky
    float hazeDirection = 0.8;       // how much the dust scatters forward, 0 to below 1: nearer 1, a tighter glow
    float moonBrightness = 10.0;     // nits of a full moon's face per lux of its light, whatever its size: a real moon's
                                     // is about 16 000, a white disc beside a ground exposed for its light; 10 shows
                                     // its face by night and leaves it all but unseen by day
    float stars = 1.0;               // how bright the stars are, 0 for none
    float starDensity = 0.004;       // the share of the sky's cells that hold a star, 0 to 1
    float starTwinkle = 0.3;         // how much the stars scintillate, 0 still to 1, the more the nearer the horizon
    TextureRef moonTexture;          // an image of the moon's face, up on the image toward the sky's up, its middle 0.5
                                     // the face's mean; none for a face of maria and craters made from noise
    Color starWarm = Color(1.0, 0.8, 0.6, 1.0);   // the tint of the reddest stars, linear
    Color starCool = Color(0.7, 0.8, 1.0, 1.0);   // the tint of the bluest stars, linear
    float moonDetail = 0.6;          // how strongly the made face shows its maria and craters, 0 a plain disc to 1
    float earthshine = 0.004;        // the light on the moon's dark side, as a share of a full moon's
    float moonSoftness = 0.03;       // how soft the line between the moon's lit and dark sides is, 0 sharp
    float moonGlow = 1.0;            // how much the haze glows around the moon, as a multiple of what the air scatters
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

// Steps along the way to a place of the world, for the air in front of it, which the renderer draws coarse and many
// times over.
static const uint AirSteps = 8u;

// Steps through the clouds' layer along the view and into the capture, which is seen coarser; taps toward the sun
// from each step with cloud in it; and taps along the sun's way through the layer to a place, for its shadow.
static const uint CloudSteps = 24u;
static const uint CaptureCloudSteps = 10u;
static const uint CloudLightTaps = 4u;
static const uint CloudShadowTaps = 6u;

// Octaves of the cover's noise along the view and for the shadow, and toward the sun and into the capture.
static const uint CoverOctaves = 3u;
static const uint CoarseCoverOctaves = 2u;

// How the clouds' optical depth ranges from the thinnest weather to the thickest, through the layer's thickness: a
// cumulus a kilometre deep is tens deep, so a cloud's edge is sharp and its middle dark.
static const float ThinCloud = 4.0;
static const float ThickCloud = 48.0;

// The least a cloud's density is, as a share of its most, where the cover only just passes the weather's coverage,
// so a sky it covers whole still shows thicker and thinner cloud.
static const float ThinnestShare = 0.5;

// The billowing noise's cells as a share of a cloud's size, and the most of a cloud's thinnest edge it eats.
static const float BillowScale = 0.08;
static const float BillowErosion = 0.7;

// The light scattered more than once: octaves of it, each with this share of the last's extinction and of its light
// (Wrenninge 2013).
static const uint CloudScatterOctaves = 3u;
static const float CloudScatterFalloff = 0.5;

// How far forward a cloud's droplets scatter on average, which sets how much light diffuses through a thick cloud
// rather than being put out: a cloud of optical depth t lets about 1 / (1 + 3/4 t (1 - g)) through, as diffusion
// theory has it (Bohren 1987, "Multiple scattering of light and some of its observable consequences"), so a thick
// overcast is dim beneath, not black, and brighter where it is thinner.
static const float CloudAsymmetry = 0.85;

// Metres past which the clouds fade out, as the layer thins to a line at the horizon, and the most of the layer a view
// ray is marched through, as one that grazes it would cross tens of kilometres of it.
static const float CloudFade = 60000.0;
static const float CloudLongest = 30000.0;

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

// The sun's limb darkening, by colour: the brightness a share mu of the way from its rim to its middle is mu to this
// power, red, green and blue, so the rim is redder (Hestroffer & Magnan 1998, "Wavelength dependency of the Solar limb
// darkening").
static const float3 LimbDarkening = float3(0.43, 0.51, 0.63);

// The share of a disc's radius its edge softens over, so it does not stair-step.
static const float DiscEdge = 0.03;

// How bright the moon's light must be beside the sun's for the air and the clouds to be lit by it too, as a share of
// the sun's that reaches them; by day it is a millionth, so it is passed over then.
static const float MoonNoticed = 1e-4;

// The made moon's face: the size of its maria and of its craters, as cells across its disc, and how much darker the
// maria are than the highlands and brighter the craters' rays, at full detail.
static const float MariaCells = 2.2;
static const float CraterCells = 14.0;
static const float MariaDark = 0.45;
static const float CraterBright = 0.35;

// Stars: cells of the sky a radian across, how fast one fades off its centre, and the brightest's nits.
static const float StarCells = 400.0;
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

// Where a ray from a place relative to the planet's centre is within the shell between two radii about it, the near
// and far distances: from beneath, out of the inner sphere and then the outer; from within, to where it leaves; from
// above, into the outer and out of the inner if it goes down into it, else out of the outer. The far is no farther
// than the near where it never is.
float2 ShellSpan(float3 origin, float3 direction, float inner, float outer)
{
    float height = length(origin);
    float2 innerHits = SphereHits(origin, direction, inner);
    float2 outerHits = SphereHits(origin, direction, outer);
    if (height < inner)
    {
        return float2(innerHits.y, outerHits.y);
    }

    if (height <= outer)
    {
        return float2(0.0, innerHits.x > 0.0 ? innerHits.x : outerHits.y);
    }

    if (outerHits.x <= 0.0)
    {
        return float2(0.0, 0.0);
    }

    return float2(outerHits.x, innerHits.x > 0.0 ? innerHits.x : outerHits.y);
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

// How much of the sun's or the moon's light, from toward towardLight, reaches a place relative to the planet's centre
// through the air, by colour; none where the planet is in the way.
float3 AirTransmittance(float3 origin, float3 towardLight, float haze)
{
    if (SphereHits(origin, towardLight, PlanetRadius).x > 0.0)
    {
        return float3(0.0, 0.0, 0.0);
    }

    float span = max(SphereHits(origin, towardLight, AtmosphereRadius).y, 0.0) / (float)LightSteps;
    float2 depth = float2(0.0, 0.0);
    [loop] for (uint i = 0u; i < LightSteps; i++)
    {
        float3 at = origin + towardLight * (((float)i + 0.5) * span);
        depth += AirDensity(length(at) - PlanetRadius) * span;
    }

    return exp(-(RayleighScattering * depth.x + MieExtinction * haze * depth.y));
}

// The clouds' cover at a place of the noise's space, cut at the weather's coverage: x how much of it is cloud, 0 to 1
// across its edge, and y how deep into the cloud it is, 0 at its edge to 1 where the noise is highest.
float2 CloudCover(float3 place, float coverage, float softness, uint octaves)
{
    float sum = 0.0;
    float total = 0.0;
    float amplitude = 0.5;
    [loop] for (uint octave = 0u; octave < octaves; octave++)
    {
        sum += GradientNoise(place) * amplitude;
        total += amplitude;
        amplitude *= 0.5;
        place = place * 2.03 + float3(17.1, 4.7, 9.3);
    }

    float noise = saturate(sum / max(total, 1e-4) * 0.8 + 0.5);
    float past = noise - (1.0 - coverage);
    return float2(saturate(past / (max(softness, 0.01) * 0.5)), saturate(past / max(coverage, 1e-3)));
}

// How much cloud there is at a place of the layer, its x and z the world's in metres and its y metres above the
// clouds' bases, 0 to 1: the cover over it, carried by the wind and changing slowly, denser and taller the deeper into
// the cover, its base rounded; and where eroded, eaten at its thin edges by billowing noise the wind carries with it,
// which also stirs its middle.
float CloudDensity(SkyInputs input, MaterialParams material, float3 at, uint octaves, bool isEroded)
{
    float scale = max(material.cloudScale, 1.0);
    float worldSec = input.frame.worldSec;
    float3 drifted = float3(at.x - input.weather.wind.x * worldSec, at.y, at.z - input.weather.wind.z * worldSec);
    float2 cover = CloudCover(float3(drifted.xz / scale, worldSec * CloudChange), input.weather.coverage, material.cloudSoftness, octaves);
    float height = at.y / max(material.cloudThickness, 1.0);
    float top = lerp(0.3, 1.0, cover.y);
    float shape = cover.x * lerp(ThinnestShare, 1.0, cover.y) * saturate(height / 0.08) * saturate((top - height) / (0.3 * top));
    [branch] if (!isEroded || shape <= 0.0)
    {
        return shape;
    }

    float3 billowPlace = drifted / (scale * BillowScale);
    float billow = (1.0 - abs(GradientNoise(billowPlace))) * 0.65 + (1.0 - abs(GradientNoise(billowPlace * 2.7 + float3(3.1, 7.7, 1.3)))) * 0.35;
    float detail = saturate(material.cloudDetail);
    float eaten = (1.0 - billow) * detail * BillowErosion;
    return saturate((shape - eaten) / max(1.0 - eaten, 1e-3)) * lerp(1.0, billow, detail * 0.5);
}

// The clouds' extinction per metre where the density is 1: the optical depth the weather's density asks for through
// the layer's thickness.
float CloudExtinction(SkyInputs input, MaterialParams material)
{
    return lerp(ThinCloud, ThickCloud, saturate(input.weather.density)) / max(material.cloudThickness, 1.0);
}

// The clouds' optical depth from a place of the layer toward a light, a few taps farther apart the farther along its
// way, of the cover's shape alone, up to the layer's top.
float CloudDepthToward(SkyInputs input, MaterialParams material, float3 at, float3 towardLight, float extinction)
{
    float depth = 0.0;
    float last = 0.0;
    [loop] for (uint i = 0u; i < CloudLightTaps; i++)
    {
        float far = material.cloudThickness * Pow2(((float)i + 1.0) / (float)CloudLightTaps);
        float3 tap = at + towardLight * (0.5 * (last + far));
        if (tap.y > material.cloudThickness)
        {
            break;
        }

        depth += CloudDensity(input, material, tap, CoarseCoverOctaves, false) * (far - last);
        last = far;
    }

    return depth * extinction;
}

// What a view ray meets of the clouds: their light toward the camera, what they let through of what is behind them,
// and how far off they are, by where along the ray their light came from.
struct CloudMarch
{
    float3 light;
    float through;
    float distance;
};

// A light the clouds are lit by along a view ray: the way toward it, its light reaching the clouds' layer through the
// air above, zero for one that lights nothing now, and its two lobes' phase toward the view, as a share of an even one's.
struct CloudLight
{
    float3 toward;
    float3 reaching;
    float phase;
    float awayFromView;
};

CloudLight CloudLightOf(float3 toward, float3 reaching, float3 direction)
{
    float cosView = dot(direction, toward);
    CloudLight light;
    light.toward = toward;
    light.reaching = reaching;
    light.phase = 4.0 * Pi * lerp(HenyeyGreenstein(cosView, CloudBack), HenyeyGreenstein(cosView, CloudForward), CloudForwardShare);
    light.awayFromView = saturate(0.5 - 0.5 * cosView);
    return light;
}

// What a light sends toward the view from a step of cloud at a place of the layer: its light through the cloud toward
// it, each octave of the light scattered more than once less put out by it, darker on the side away from it, and what
// diffuses through the cloud beyond those, spread every way, so it takes no lobe.
float3 CloudLit(SkyInputs input, MaterialParams material, float3 at, CloudLight light, float extinction, float stepDepth)
{
    [branch] if (all(light.reaching <= 0.0))
    {
        return float3(0.0, 0.0, 0.0);
    }

    float toLight = CloudDepthToward(input, material, at, light.toward, extinction);
    float reached = 0.0;
    float total = 0.0;
    float share = 1.0;
    float thinned = 1.0;
    [unroll] for (uint octave = 0u; octave < CloudScatterOctaves; octave++)
    {
        reached += share * exp(-toLight * thinned);
        total += share;
        share *= CloudScatterFalloff;
        thinned *= CloudScatterFalloff;
    }

    float diffused = max(1.0 / (1.0 + 0.75 * toLight * (1.0 - CloudAsymmetry)) - exp(-toLight), 0.0);
    float powder = lerp(1.0, 1.0 - exp(-2.0 * stepDepth), light.awayFromView);
    return input.weather.cloudColor * light.reaching * ((reached / total * powder * light.phase + diffused) * CloudAlbedo * InvPi);
}

// The clouds along a view ray from a place relative to the planet's centre, through the span of their layer it
// crosses, each step lit by the sun and the moon through the cloud toward each, and by the sky; faded out toward the
// horizon.
CloudMarch MarchClouds(SkyInputs input, MaterialParams material, float3 origin, float3 direction, float2 span, CloudLight sun, CloudLight moon)
{
    CloudMarch march;
    march.light = float3(0.0, 0.0, 0.0);
    march.through = 1.0;
    march.distance = span.x;

    uint steps = input.isCapture ? CaptureCloudSteps : CloudSteps;
    float stride = (span.y - span.x) / (float)steps;
    float extinction = CloudExtinction(input, material);
    float baseRadius = PlanetRadius + material.cloudHeight;
    float3 ambient = input.weather.cloudShadow * ((sun.reaching + moon.reaching) * AmbientPerLux + material.nightSky.rgb);
    float weighted = 0.0;
    [loop] for (uint i = 0u; i < steps && march.through > 0.01; i++)
    {
        float along = span.x + ((float)i + input.dither) * stride;
        float3 place = origin + direction * along;
        float3 at = float3(input.cameraPosition.x + place.x, length(place) - baseRadius, input.cameraPosition.z + place.z);
        float density = CloudDensity(input, material, at, input.isCapture ? CoarseCoverOctaves : CoverOctaves, !input.isCapture);
        [branch] if (density <= 0.0)
        {
            continue;
        }

        float stepDepth = density * extinction * stride;
        float stepOpacity = 1.0 - exp(-stepDepth);
        float3 lit = CloudLit(input, material, at, sun, extinction, stepDepth) + CloudLit(input, material, at, moon, extinction, stepDepth);
        float3 skylit = ambient * lerp(0.6, 1.0, saturate(at.y / max(material.cloudThickness, 1.0)));
        float gathered = march.through * stepOpacity;
        march.light += (lit + skylit) * gathered;
        weighted += along * gathered;
        march.through *= 1.0 - stepOpacity;
    }

    float opacity = 1.0 - march.through;
    march.distance = opacity > 0.0 ? weighted / opacity : span.x;
    float fade = saturate(1.0 - march.distance / CloudFade);
    march.light *= fade;
    march.through = 1.0 - opacity * fade;
    return march;
}

// A star's light along a direction: the share of the sky's cells the material asks for holds one, somewhere in the
// cell, most of them faint and a few bright, tinted between the material's two colours, and twinkling, the more the
// nearer the horizon.
float3 StarLight(float3 direction, MaterialParams material, float timeSec)
{
    float density = saturate(material.starDensity);
    float3 cell = floor(direction * StarCells);
    uint hash = LatticeHash(int3(cell));
    float chance = HashToUnit(hash);
    if (density <= 0.0 || chance < 1.0 - density)
    {
        return float3(0.0, 0.0, 0.0);
    }

    float3 jitter = float3(HashToUnit(PcgHash(hash)), HashToUnit(PcgHash(hash + 1u)), HashToUnit(PcgHash(hash + 2u)));
    float3 star = normalize((cell + jitter) / StarCells);
    float rank = (chance - (1.0 - density)) / density;
    float brightness = exp(-(1.0 - dot(direction, star)) * StarSharpness) * Pow2(Pow2(rank));
    float flicker = 0.5 + 0.5 * sin(timeSec * (6.0 + 6.0 * HashToUnit(PcgHash(hash + 4u))) + TwoPi * HashToUnit(PcgHash(hash + 5u)));
    float lowness = 1.0 - saturate(direction.y * 4.0);
    float twinkle = 1.0 - saturate(material.starTwinkle) * flicker * lerp(0.3, 1.0, lowness);
    float3 tint = lerp(material.starWarm.rgb, material.starCool.rgb, HashToUnit(PcgHash(hash + 3u)));
    return tint * (brightness * twinkle * StarNits);
}

// The share of a full moon's light the moon sends the Earth, by its phase: the sun lights it from an angle round from
// behind the Earth, and the share of a sphere's lit side that shows from here falls from all of it full to none new
// (Lambert's sphere). All of it where the world has no sun.
float MoonPhaseShare(float3 towardSun, float3 towardMoon, bool hasSun)
{
    if (!hasSun)
    {
        return 1.0;
    }

    float cosAngle = clamp(dot(towardSun, -towardMoon), -1.0, 1.0);
    float angle = acos(cosAngle);
    return saturate((sin(angle) + (Pi - angle) * cosAngle) / Pi);
}

// The moon's face as the sky sees it: where a direction falls on its disc, -1 to 1 across it, and the way right and
// up across the disc, up as near the sky's up as it can be.
struct MoonFace
{
    float2 at;
    float3 right;
    float3 up;
};

MoonFace MoonFaceAt(float3 direction, float3 towardMoon, float radius)
{
    float3 reference = abs(towardMoon.y) < 0.99 ? float3(0.0, 1.0, 0.0) : float3(0.0, 0.0, 1.0);
    MoonFace face;
    face.right = normalize(cross(reference, towardMoon));
    face.up = cross(towardMoon, face.right);
    float3 off = direction - towardMoon * dot(direction, towardMoon);
    face.at = float2(dot(off, face.right), dot(off, face.up)) / max(sin(radius), 1e-6);
    return face;
}

// The moon's face at a point of its disc, by colour, relative to its mean: the material's image of it, else maria and
// craters made from noise over its sphere. faceDdx and faceDdy are how the point moves across a pixel.
float3 MoonAlbedo(MaterialParams material, float2 at, float3 onSphere, float2 faceDdx, float2 faceDdy)
{
    [branch] if (HasTexture(material.moonTexture))
    {
        float2 flip = float2(0.5, -0.5);
        return SampleTextureGrad(material.moonTexture, at * flip + 0.5, faceDdx * flip, faceDdy * flip).rgb * 2.0;
    }

    float detail = saturate(material.moonDetail);
    float maria = smoothstep(-0.1, 0.35, GradientNoise(onSphere * MariaCells + float3(3.7, 1.9, 5.3)));
    float craters = Pow2(saturate(GradientNoise(onSphere * CraterCells + float3(11.3, 2.1, 7.9)) * 2.0 - 0.6));
    return (1.0 + detail * (MariaDark * (0.5 - maria) + CraterBright * craters)).xxx;
}

// The moon's disc along a direction, in nits before the air: each point lit from the sun's side as the moon's dust
// scatters light (Lommel-Seeliger, so a full moon is evenly bright to its rim), a full moon's face as bright as the
// material says for its light's illuminance, its terminator softened, its dark side lit by the Earth, its edge
// softened; zero off it.
float3 MoonDisc(SkyInputs input, MaterialParams material, MoonFace face, float2 faceDdx, float2 faceDdy, float3 towardMoon, float3 towardSun, bool hasSun)
{
    float off = length(face.at);
    if (off >= 1.0 || all(input.lights.moonIlluminance <= 0.0))
    {
        return float3(0.0, 0.0, 0.0);
    }

    float toEarth = sqrt(1.0 - off * off);
    float3 normal = face.right * face.at.x + face.up * face.at.y - towardMoon * toEarth;
    float toSun = hasSun ? dot(normal, towardSun) : toEarth;
    float softness = max(material.moonSoftness, 1e-3);
    float scatter = 2.0 * max(toSun, 0.0) / max(max(toSun, 0.0) + toEarth, 1e-4) * smoothstep(-softness, softness, toSun);
    float3 albedo = MoonAlbedo(material, face.at, float3(face.at, toEarth), faceDdx, faceDdy);
    float3 full = input.lights.moonIlluminance * max(material.moonBrightness, 0.0);
    float edge = saturate((1.0 - off) / DiscEdge);
    return full * albedo * ((scatter + max(material.earthshine, 0.0)) * edge);
}

// The moon's light as its phase leaves it, while it is noticed beside the sun's: by night, and at dusk, but never by
// day, when working it out would change nothing; none otherwise.
float3 NoticedMoonLux(SkyInputs input, float3 towardSun, float3 towardMoon)
{
    float3 moonLux = input.lights.moonIlluminance * MoonPhaseShare(towardSun, towardMoon, any(input.lights.sunIlluminance > 0.0));
    float sunShowing = Luminance(input.lights.sunIlluminance) * saturate(towardSun.y * 10.0 + 1.0);
    bool isNoticed = Luminance(moonLux) > MoonNoticed * sunShowing && Luminance(moonLux) > 0.0 && towardMoon.y > -0.2;
    return isNoticed ? moonLux : float3(0.0, 0.0, 0.0);
}

// The air along a view ray from a place relative to the planet's centre, between two distances along it: the light it
// scatters toward the start, and what it lets through, by colour.
struct AirSpan
{
    float3 light;
    float3 through;
};

// The air along a span of a view ray, in steps closer together near its start, where the air is thickest along most
// rays, each step's light integrated across it as its own transmittance falls: the sun's, and the moon's where moonLux
// has any, the haze's glow about it as strong as the material asks.
AirSpan AirAlong(
    float3 origin,
    float3 direction,
    float from,
    float to,
    uint steps,
    float3 towardSun,
    float3 sunLux,
    float3 towardMoon,
    float3 moonLux,
    MaterialParams material)
{
    float haze = max(material.haze, 0.0);
    float hazeLobe = clamp(material.hazeDirection, 0.0, 0.99);
    float cosSun = dot(direction, towardSun);
    float cosMoon = dot(direction, towardMoon);
    float rayleighPhase = RayleighPhase(cosSun);
    float miePhase = HenyeyGreenstein(cosSun, hazeLobe);
    float moonRayleighPhase = RayleighPhase(cosMoon);
    float moonMiePhase = HenyeyGreenstein(cosMoon, hazeLobe) * max(material.moonGlow, 0.0);
    float evenPhase = 1.0 / (4.0 * Pi);
    bool hasMoon = any(moonLux > 0.0);
    AirSpan air;
    air.light = float3(0.0, 0.0, 0.0);
    air.through = float3(1.0, 1.0, 1.0);
    float span = max(to - from, 0.0);
    [loop] for (uint i = 0u; i < steps && span > 0.0; i++)
    {
        float stepStart = from + span * Pow2((float)i / (float)steps);
        float stepEnd = from + span * Pow2((float)(i + 1u) / (float)steps);
        float3 place = origin + direction * (0.5 * (stepStart + stepEnd));
        float2 density = AirDensity(length(place) - PlanetRadius);
        float3 rayleigh = RayleighScattering * density.x;
        float3 mie = float3(1.0, 1.0, 1.0) * (MieScattering * haze * density.y);
        float3 extinction = rayleigh + float3(1.0, 1.0, 1.0) * (MieExtinction * haze * density.y);
        float3 scattered = AirTransmittance(place, towardSun, haze) * (rayleigh * rayleighPhase + mie * miePhase + (rayleigh + mie) * evenPhase) * sunLux;
        [branch] if (hasMoon)
        {
            scattered += AirTransmittance(place, towardMoon, haze) * (rayleigh * moonRayleighPhase + mie * moonMiePhase + (rayleigh + mie) * evenPhase) * moonLux;
        }

        float3 stepThrough = exp(-extinction * (stepEnd - stepStart));
        air.light += air.through * scattered * (1.0 - stepThrough) / max(extinction, 1e-12);
        air.through *= stepThrough;
    }

    return air;
}

SkyLight EvaluateSky(SkyInputs input, MaterialParams material)
{
    float3 direction = input.direction;
    float3 towardSun = -input.lights.sunDirection;
    float3 towardMoon = -input.lights.moonDirection;
    float3 sunLux = input.lights.sunIlluminance;
    bool hasSun = any(sunLux > 0.0);
    float haze = max(material.haze, 0.0);
    float cosSun = dot(direction, towardSun);
    float cosMoon = dot(direction, towardMoon);
    float3 origin = float3(0.0, PlanetRadius + max(input.cameraPosition.y, 0.0) + 1.0, 0.0);

    // Where the view falls on the moon's face, and how it moves across a pixel, taken before anything branches.
    MoonFace face = MoonFaceAt(direction, towardMoon, max(input.lights.moonAngularRadius, 1e-6));
    float2 faceDdx = ddx(face.at);
    float2 faceDdy = ddy(face.at);

    // The moon's light, while it is noticed beside the sun's.
    float3 moonLux = NoticedMoonLux(input, towardSun, towardMoon);
    bool isMoonNoticed = any(moonLux > 0.0);

    // Where the view ray is in the air: from the camera, or where it comes into the air from above, to where it leaves
    // it or meets the ground.
    float2 air = SphereHits(origin, direction, AtmosphereRadius);
    float2 ground = SphereHits(origin, direction, PlanetRadius);
    bool isGround = ground.x > 0.0;
    float rayStart = max(air.x, 0.0);
    float rayEnd = isGround ? ground.x : max(air.y, 0.0);

    // The clouds along it, through the span of their layer it crosses before the ground, and no more than so far of it,
    // lit by the sun and the moon through the air above where it comes into them.
    float baseRadius = PlanetRadius + material.cloudHeight;
    float2 layer = ShellSpan(origin, direction, baseRadius, baseRadius + max(material.cloudThickness, 1.0));
    layer.y = min(min(layer.y, rayEnd), layer.x + CloudLongest);
    bool hasClouds = input.weather.coverage > 0.0 && layer.y > layer.x && layer.x < CloudFade;
    CloudMarch clouds;
    clouds.light = float3(0.0, 0.0, 0.0);
    clouds.through = 1.0;
    clouds.distance = rayEnd;
    if (hasClouds)
    {
        float3 entry = origin + direction * layer.x;
        float3 moonOnCloud = isMoonNoticed ? AirTransmittance(entry, towardMoon, haze) * moonLux : float3(0.0, 0.0, 0.0);
        CloudLight sunLight = CloudLightOf(towardSun, AirTransmittance(entry, towardSun, haze) * sunLux, direction);
        CloudLight moonLight = CloudLightOf(towardMoon, moonOnCloud, direction);
        clouds = MarchClouds(input, material, origin, direction, layer, sunLight, moonLight);
    }

    float splitAt = hasClouds ? clouds.distance : rayEnd;

    // The air's light in front of the clouds and past them, the steps shared between the two where there are clouds.
    uint steps = input.isCapture ? CaptureSteps : ViewSteps;
    uint frontSteps = hasClouds ? steps / 2u : steps;
    AirSpan front = AirAlong(origin, direction, rayStart, splitAt, frontSteps, towardSun, sunLux, towardMoon, moonLux, material);
    AirSpan past = AirAlong(origin, direction, splitAt, rayEnd, steps - frontSteps, towardSun, sunLux, towardMoon, moonLux, material);
    float3 through = front.through * past.through;

    // What is past the clouds besides the air's light: the ground, or the night sky and its stars.
    float3 beyond = front.through * past.light;
    if (isGround)
    {
        float3 landed = origin + direction * rayEnd;
        float3 up = normalize(landed);
        float3 sunOnGround = AirTransmittance(landed + up, towardSun, haze) * sunLux;
        beyond += through * material.groundColor.rgb * sunOnGround * (saturate(dot(up, towardSun)) * InvPi);
    }
    else
    {
        float day = hasSun ? saturate(towardSun.y * 10.0 + 0.5) : 0.0;
        beyond += through * (material.nightSky.rgb + StarLight(direction, material, input.frame.worldSec) * (max(material.stars, 0.0) * (1.0 - day)));
    }

    SkyLight light;
    light.nits = front.light + front.through * clouds.light + beyond * clouds.through;
    light.emissive = float3(0.0, 0.0, 0.0);
    light.discs = float3(0.0, 0.0, 0.0);
    if (isGround)
    {
        return light;
    }

    // The sun's disc, darker and redder toward its rim, each colour kept to the disc's whole light (the mean of mu to a
    // power p over a disc is 2 / (p + 2)), its edge softened, through the air and the clouds in front of it.
    float radius = max(input.lights.sunAngularRadius, 1e-6);
    float off = sqrt(max(2.0 * (1.0 - cosSun), 0.0)) / radius;
    if (hasSun && off < 1.0)
    {
        float mu = sqrt(1.0 - off * off);
        float3 limb = pow(max(mu, 1e-4).xxx, LimbDarkening) * ((LimbDarkening + 2.0) * 0.5);
        float edge = saturate((1.0 - off) / DiscEdge);
        light.discs += sunLux * limb * (edge / (Pi * radius * radius));
    }

    // The moon's, added to the sky's light in front of it.
    light.discs += MoonDisc(input, material, face, faceDdx, faceDdy, towardMoon, towardSun, hasSun);
    light.discs *= through * clouds.through;
    return light;
}

// What reaches a place of the sun's or the moon's light from toward towardLight, by colour: what the air lets through,
// none where the ground is in the way, times what the clouds let through, taps of the same cover the view sees, its
// shape without the billows' erosion, so the ground beneath the camera is dimmed as the disc is.
float3 LightThrough(SkyInputs input, float3 position, float3 towardLight, MaterialParams material)
{
    float3 origin = float3(0.0, PlanetRadius + max(position.y, 0.0) + 1.0, 0.0);
    float3 air = AirTransmittance(origin, towardLight, max(material.haze, 0.0));
    float baseRadius = PlanetRadius + material.cloudHeight;
    float2 layer = ShellSpan(origin, towardLight, baseRadius, baseRadius + max(material.cloudThickness, 1.0));
    layer.y = min(layer.y, layer.x + CloudLongest);
    float fade = saturate(1.0 - layer.x / CloudFade);
    if (input.weather.coverage <= 0.0 || layer.y <= layer.x || fade <= 0.0 || all(air <= 0.0))
    {
        return air;
    }

    float stride = (layer.y - layer.x) / (float)CloudShadowTaps;
    float depth = 0.0;
    [loop] for (uint i = 0u; i < CloudShadowTaps; i++)
    {
        float3 place = origin + towardLight * (layer.x + ((float)i + 0.5) * stride);
        float3 at = float3(position.x + place.x, length(place) - baseRadius, position.z + place.z);
        depth += CloudDensity(input, material, at, CoverOctaves, false) * stride;
    }

    return air * (1.0 - (1.0 - exp(-depth * CloudExtinction(input, material))) * fade);
}

// The air between the camera and a place of the world along the view: the same air the sky is seen through, so a far
// mountain fades into the light the sky shows past it. Clouds in front of the place are left out.
AirLight EvaluateAir(SkyInputs input, float reach, MaterialParams material)
{
    float3 towardSun = -input.lights.sunDirection;
    float3 towardMoon = -input.lights.moonDirection;
    float3 origin = float3(0.0, PlanetRadius + max(input.cameraPosition.y, 0.0) + 1.0, 0.0);
    float3 moonLux = NoticedMoonLux(input, towardSun, towardMoon);
    AirSpan span = AirAlong(origin, input.direction, 0.0, reach, AirSteps, towardSun, input.lights.sunIlluminance, towardMoon, moonLux, material);
    AirLight air;
    air.nits = span.light;
    air.through = dot(span.through, float3(1.0, 1.0, 1.0) / 3.0);
    return air;
}
