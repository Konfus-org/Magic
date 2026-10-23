using Magic.Contexts.Components;

namespace Magic.SteamAudio;

/// <summary>
/// What each <see cref="AcousticMaterial"/> does to sound, as Steam Audio measures it in three bands, low, middle and
/// high: how much of what reaches it is soaked up, how much of the rest is scattered rather than reflected like a
/// mirror, and how much passes through. Steam Audio's own presets.
/// </summary>
internal static unsafe class Materials
{
    internal static IPLMaterial Get(AcousticMaterial material)
    {
        return material switch
        {
            AcousticMaterial.Brick => Create(0.03f, 0.04f, 0.07f, 0.05f, 0.015f, 0.015f, 0.015f),
            AcousticMaterial.Concrete => Create(0.05f, 0.07f, 0.08f, 0.05f, 0.015f, 0.002f, 0.001f),
            AcousticMaterial.Ceramic => Create(0.01f, 0.02f, 0.02f, 0.05f, 0.060f, 0.044f, 0.011f),
            AcousticMaterial.Gravel => Create(0.60f, 0.70f, 0.80f, 0.05f, 0.031f, 0.012f, 0.008f),
            AcousticMaterial.Carpet => Create(0.24f, 0.69f, 0.73f, 0.05f, 0.020f, 0.005f, 0.003f),
            AcousticMaterial.Glass => Create(0.06f, 0.03f, 0.02f, 0.05f, 0.060f, 0.044f, 0.011f),
            AcousticMaterial.Plaster => Create(0.12f, 0.06f, 0.04f, 0.05f, 0.056f, 0.056f, 0.004f),
            AcousticMaterial.Wood => Create(0.11f, 0.07f, 0.06f, 0.05f, 0.070f, 0.014f, 0.005f),
            AcousticMaterial.Metal => Create(0.20f, 0.07f, 0.06f, 0.05f, 0.200f, 0.025f, 0.010f),
            AcousticMaterial.Rock => Create(0.13f, 0.20f, 0.24f, 0.05f, 0.015f, 0.002f, 0.001f),
            AcousticMaterial.Generic => Create(0.10f, 0.20f, 0.30f, 0.05f, 0.100f, 0.050f, 0.030f),
            _ => Create(0.10f, 0.20f, 0.30f, 0.05f, 0.100f, 0.050f, 0.030f),
        };
    }

    private static IPLMaterial Create(
        float absorbLow,
        float absorbMid,
        float absorbHigh,
        float scattering,
        float passLow,
        float passMid,
        float passHigh)
    {
        IPLMaterial material = default;
        material.absorption[0] = absorbLow;
        material.absorption[1] = absorbMid;
        material.absorption[2] = absorbHigh;
        material.scattering = scattering;
        material.transmission[0] = passLow;
        material.transmission[1] = passMid;
        material.transmission[2] = passHigh;

        return material;
    }
}
