namespace Magic.Graphics;

/// <summary>
/// An effect at its place in a frame's lists: its material, ready when its material, textures and passes are, its passes'
/// entries specialised to its shader, and what it declares it needs. Its material's row is the view's effect row at
/// its place.
/// </summary>
public readonly record struct GpuEffect(
    ulong Material,
    GpuState State,
    ShaderEntry Apply,
    ShaderEntry Level,
    EffectNeeds Needs
);

/// <summary>
/// What an effect's shader declares it needs: levels of a target of its own, the camera jittered, and rays.
/// </summary>
public readonly record struct EffectNeeds(int Levels, bool HasJitter, bool HasRays);
