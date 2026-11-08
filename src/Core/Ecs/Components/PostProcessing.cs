namespace Magic.Ecs.Components;

/// <summary>
/// What is done to the picture after the lighting: its post effects, each a material whose shader implements the
/// pixel effect contract, run in order, the exposure and the tonemap among them. It goes on any entity, and the
/// renderer takes the first it finds; with none, the picture gets the renderer's default effects.
/// </summary>
public record struct PostProcessing : IComponent
{
    public MaterialSlots Effects;
}
