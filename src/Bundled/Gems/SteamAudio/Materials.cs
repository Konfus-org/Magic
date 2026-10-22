using Magic.Contexts.Components;

namespace Magic.SteamAudio;

/// <summary>
/// What each <see cref="AcousticMaterial"/> does to sound, as Steam Audio measures it in three bands, low, middle and
/// high: how much of what reaches it is soaked up, how much of the rest is scattered rather than reflected like a
/// mirror, and how much passes through. Steam Audio's own presets.
/// </summary>
internal static unsafe class Materials
{
    internal static IPL.Material Get(AcousticMaterial material)
    {
        return material switch
        {
            AcousticMaterial.Brick => Make(0.03f, 0.04f, 0.07f, 0.05f, 0.015f, 0.015f, 0.015f),
            AcousticMaterial.Concrete => Make(0.05f, 0.07f, 0.08f, 0.05f, 0.015f, 0.002f, 0.001f),
            AcousticMaterial.Ceramic => Make(0.01f, 0.02f, 0.02f, 0.05f, 0.060f, 0.044f, 0.011f),
            AcousticMaterial.Gravel => Make(0.60f, 0.70f, 0.80f, 0.05f, 0.031f, 0.012f, 0.008f),
            AcousticMaterial.Carpet => Make(0.24f, 0.69f, 0.73f, 0.05f, 0.020f, 0.005f, 0.003f),
            AcousticMaterial.Glass => Make(0.06f, 0.03f, 0.02f, 0.05f, 0.060f, 0.044f, 0.011f),
            AcousticMaterial.Plaster => Make(0.12f, 0.06f, 0.04f, 0.05f, 0.056f, 0.056f, 0.004f),
            AcousticMaterial.Wood => Make(0.11f, 0.07f, 0.06f, 0.05f, 0.070f, 0.014f, 0.005f),
            AcousticMaterial.Metal => Make(0.20f, 0.07f, 0.06f, 0.05f, 0.200f, 0.025f, 0.010f),
            AcousticMaterial.Rock => Make(0.13f, 0.20f, 0.24f, 0.05f, 0.015f, 0.002f, 0.001f),
            AcousticMaterial.Generic => Make(0.10f, 0.20f, 0.30f, 0.05f, 0.100f, 0.050f, 0.030f),
            _ => Make(0.10f, 0.20f, 0.30f, 0.05f, 0.100f, 0.050f, 0.030f),
        };
    }

    private static IPL.Material Make(
        float absorbLow,
        float absorbMid,
        float absorbHigh,
        float scattering,
        float passLow,
        float passMid,
        float passHigh)
    {
        IPL.Material material = default;
        material.Absorption[0] = absorbLow;
        material.Absorption[1] = absorbMid;
        material.Absorption[2] = absorbHigh;
        material.Scattering = scattering;
        material.Transmission[0] = passLow;
        material.Transmission[1] = passMid;
        material.Transmission[2] = passHigh;

        return material;
    }
}
