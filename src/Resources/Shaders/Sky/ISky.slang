// The sky contract: a module implements it in a struct, the sky's material names that module, and the lighting pass
// specialised to the struct draws the sky where nothing else is and asks it what the sun and the sky give the
// ground.
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
    // The heap ids and sampler of whatever the sky made ahead of the frame.
    public uint transmittanceLut;
    public uint multiScatteringLut;
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
