// The air of the world: a haze that scatters the sun's and the sky's light, thinning by half every halfHeight above its
// height, so the far fades into it. A fog material's params are this struct's fields by name, "density", "halfHeight",
// "height", "color", "anisotropy" and "sun". It draws as a pixel effect, before the post effects.
module "Weather/Fog";

import Core.Math;
import Post.IPixelEffect;

// Under this much rise along a ray, in units of the falloff, the air is taken as level, where the closed form divides
// by nothing.
static const float LevelRise = 1e-4;

// How much of the light the air scatters goes off at an angle from its path, per steradian, by the cosine of that
// angle: evenly at an anisotropy of 0, towards 1 mostly onward, by the Henyey-Greenstein phase function (Henyey and
// Greenstein, "Diffuse radiation in the galaxy", 1941).
public float GetScatteringPhase(float anisotropy, float cosine)
{
    float g = clamp(anisotropy, -0.99, 0.99);
    return (1.0 - g * g) / (4.0 * Pi * pow(max(1.0 + g * g - 2.0 * g * cosine, 1e-4), 1.5));
}

public struct Fog : IPixelEffect
{
    // How much of the light a metre of air at its height scatters, 0 to 1.
    public float density;
    // Metres above its height at which the air is half as thick.
    public float halfHeight;
    // Where the air is as thick as density says, in metres.
    public float height;
    // What the air scatters light into: white for clean haze.
    public float4 color;
    // How much the air scatters light onward rather than everywhere, -1 to 1: 0 evenly, towards 1 mostly ahead, as
    // mist does.
    public float anisotropy;
    // How much of the sun's light the fog scatters itself, everywhere as if nothing stood in its way: 1, or 0 where the
    // volumetric light post effect scatters it only where the air sees the sun.
    public float sun;

    public float3 Apply(float3 picture, PixelEffectInputs inputs)
    {
        // The air between the camera and the nearest surface, a pane of glass included, so a pane keeps its own colour,
        // its density falling off exponentially with height, integrated in closed form along the ray (Wenzel,
        // "Real-Time Atmospheric Effects in Games", 2006).
        float falloff = 0.693147 / max(halfHeight, 0.01);
        float distance = length(inputs.nearestPosition);
        float3 direction = inputs.nearestPosition / max(distance, 1e-6);
        // Clamped going down only, where exp(-rise) would overflow; going up it falls to 0 and the form stays exact.
        float rise = max(direction.y * distance * falloff, -80.0);
        float atCamera = density * exp(-falloff * (inputs.cameraPosition.y - height));
        float opticalDepth = atCamera * distance * (abs(rise) > LevelRise ? (1.0 - exp(-rise)) / rise : 1.0);
        float transmittance = exp(-opticalDepth);

        // What the air scatters towards the camera: the sky's light evenly and the sun's by the angle to it.
        float phase = GetScatteringPhase(anisotropy, dot(direction, inputs.sunDirection));
        float3 scattered = color.rgb * (inputs.skyRadiance + sun * inputs.sunRadiance * phase);
        return picture * transmittance + scattered * (1.0 - transmittance);
    }
}
