using Magic.Graphics;

namespace Magic.PostEffects;

/// <summary>
/// An effect at its place in a frame's list: its material, its passes' entries specialised to its shader, and its
/// needs.
/// </summary>
/// <remarks>
/// It is ready when its material, textures and passes are. Its material's row is the frame's effect row at its place.
/// </remarks>
internal readonly record struct GpuEffect(
    ulong Material,
    GpuState State,
    ShaderEntry Apply,
    ShaderEntry Level,
    EffectNeeds Needs
);

/// <summary>
/// What an effect's shader declares it needs: levels of a target of its own, last frame's target as its history, and
/// the camera jittered.
/// </summary>
internal readonly record struct EffectNeeds(int Levels, bool HasHistory, bool HasJitter);
