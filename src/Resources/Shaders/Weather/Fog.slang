// The air of the world: a haze that scatters the sun's and the sky's light, thinning by half every halfHeight above its
// height, so the far fades into it. A fog material's params are this struct's fields by name, "density", "halfHeight",
// "height", "color" and "anisotropy". It draws as a pixel effect, before the post effects.
module "Weather/Fog";

import Core.Math;
import Post.IPixelEffect;

// Under this much rise along a ray, in units of the falloff, the air is taken as level, where the closed form divides
// by nothing.
static const float LevelRise = 1e-4;

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

    public float3 Apply(float3 picture, PixelEffectInputs inputs)
    {
        // The air between the camera and the surface, its density falling off exponentially with height, integrated in
        // closed form along the ray (Wenzel, "Real-Time Atmospheric Effects in Games", 2006).
        float falloff = 0.693147 / max(halfHeight, 0.01);
        float distance = length(inputs.position);
        float3 direction = inputs.position / max(distance, 1e-6);
        // Clamped going down only, where exp(-rise) would overflow; going up it falls to 0 and the form stays exact.
        float rise = max(direction.y * distance * falloff, -80.0);
        float atCamera = density * exp(-falloff * (inputs.cameraPosition.y - height));
        float opticalDepth = atCamera * distance * (abs(rise) > LevelRise ? (1.0 - exp(-rise)) / rise : 1.0);
        float transmittance = exp(-opticalDepth);

        // What the air scatters towards the camera: the sky's light evenly and the sun's by the Henyey-Greenstein
        // phase function (Henyey and Greenstein, "Diffuse radiation in the galaxy", 1941).
        float g = clamp(anisotropy, -0.99, 0.99);
        float cosine = dot(direction, inputs.sunDirection);
        float phase = (1.0 - g * g) / (4.0 * Pi * pow(max(1.0 + g * g - 2.0 * g * cosine, 1e-4), 1.5));
        float3 scattered = color.rgb * (inputs.skyRadiance + inputs.sunRadiance * phase);
        return picture * transmittance + scattered * (1.0 - transmittance);
    }
}
