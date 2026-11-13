namespace Magic.Ecs.Components;

/// <summary>
/// The post effects that change the picture after the lighting, in the order that they run.
/// </summary>
/// <remarks>
/// Each effect is a material whose shader implements the pixel effect contract. The exposure and the tonemap are post
/// effects too. It goes on any entity, and the renderer uses the first that it finds. With none, the picture gets the
/// default effects of the renderer.
/// </remarks>
public record struct PostProcessing : IComponent
{
    public MaterialSlots Effects;
}
