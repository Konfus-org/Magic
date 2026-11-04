using Magic.Assets;
using Magic.Assets.Types;

namespace Magic.Ecs.Components;

/// <summary>
/// What a <see cref="Camera"/> does to its picture after the lighting: up to four post effects, each a shader that
/// implements the pixel effect contract, run in order, and how the exposure meter and the bloom are set. A camera
/// without one draws with the defaults.
/// </summary>
public record struct PostProcessing : IComponent
{
    public Handle<Shader> Effect0;

    public Handle<Shader> Effect1;

    public Handle<Shader> Effect2;

    public Handle<Shader> Effect3;

    /// <summary>
    /// Stops added to what the meter picks: positive brightens.
    /// </summary>
    public float ExposureCompensation;

    /// <summary>
    /// How much of the bloom is added to the picture, 0 for none.
    /// </summary>
    public float BloomStrength = 0.04f;

    public PostProcessing() { }
}
