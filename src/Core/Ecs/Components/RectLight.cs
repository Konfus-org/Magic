using Magic.Assets.Types;

namespace Magic.Ecs.Components;

/// <summary>
/// Light from a rectangle, like a window or a panel, facing along its entity's +Z and sized by
/// <see cref="Width"/> and <see cref="Height"/> in metres before the entity's scale. <see cref="Intensity"/>, in
/// candela as from its centre, scales <see cref="Color"/>; it lights nothing behind it.
/// </summary>
public record struct RectLight : IComponent
{
    public Color Color = new(1, 1, 1);

    public float Intensity = 255;

    public float Width = 1;

    public float Height = 1;

    /// <summary>
    /// Metres it reaches; 0 for as far as its intensity is seen.
    /// </summary>
    public float Range;

    public bool IsShadowless;

    public RectLight() { }

    public readonly float Reach => Range == 0 ? SpotLight.GetReach(Intensity) : Range;
}
