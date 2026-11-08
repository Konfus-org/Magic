namespace Magic.Ecs.Components;

/// <summary>
/// What is done to the picture after the lighting: its post effects, each a material whose shader implements the
/// pixel effect contract, run in order, and how the exposure meter is set. It goes on any entity, and the renderer
/// takes the first it finds; with none, the picture gets the defaults.
/// </summary>
public record struct PostProcessing : IComponent
{
    public MaterialSlots Effects;

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
