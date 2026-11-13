// The sky contract: a module implements it in a struct, the sky's step hands the struct on with its tables, and the
// lighting pass specialised to the struct draws the sky where nothing else is and asks it what the sun and the sky give
// the ground. OpenSky is the sky of a frame that has no sky's tables.
module "Sky/ISky";

public struct SkyInputs
{
    // Unit, in the world.
    public float3 viewDirection;
    public float3 sunDirection;
    // The sun's illuminance above the air, in the frame's units.
    public float3 sunIlluminance;
    // The viewer's height above the ground, in metres.
    public float altitude;
    // Whether the sun's disc is in view: it is for what is seen, not for what a ray gathers for a surface, which
    // has the sun from its lighting already.
    public bool hasSunDisc;
    // The open sky's radiance a lux of the sun gives, for a sky without tables.
    public float3 openSkyRadiance;
    // Where the direction is down the screen, from 0 at the top to 1 at the bottom, for the sky that is seen; 0.5 for
    // any other direction.
    public float screenY;
    // The heap ids and sampler of whatever the sky made ahead of the frame.
    public uint transmittanceLut;
    public uint skyViewLut;
    public uint sampler;
}

public interface ISky
{
    // The radiance along a direction.
    static float3 Evaluate(SkyInputs inputs);

    // The sun's illuminance once through the air, at the viewer.
    static float3 GetSunIlluminance(SkyInputs inputs);

    // The sky's irradiance on a surface facing a direction.
    static float3 GetAmbient(SkyInputs inputs, float3 normal);
}

// The sky where no sky made its tables: an even light that the sun's brightness scales, brighter towards the top of
// the screen where it is seen, the sun as it is, and an ambient light from above.
public struct OpenSky : ISky
{
    public static float3 Evaluate(SkyInputs inputs)
    {
        return inputs.openSkyRadiance * inputs.sunIlluminance.r * lerp(0.6, 1.4, 1.0 - inputs.screenY);
    }

    public static float3 GetSunIlluminance(SkyInputs inputs)
    {
        return inputs.sunIlluminance;
    }

    public static float3 GetAmbient(SkyInputs inputs, float3 normal)
    {
        return inputs.sunIlluminance * 0.1 * (0.5 + 0.5 * normal.y);
    }
}
