using Magic.Files.Types;

namespace Magic.Ecs.Components;

/// <summary>
/// The air of the world: a haze that scatters the lights, thickest at <see cref="Height"/> and thinning above it, so
/// a light's beam shows and the far fades. A world has one, on an entity of its own; none leaves the air clear.
/// </summary>
public record struct Fog : IComponent
{
    /// <summary>
    /// How much of the light a metre of air at <see cref="Height"/> scatters, 0 to 1.
    /// </summary>
    public float Density = 0.02f;

    /// <summary>
    /// Metres above <see cref="Height"/> at which the air is half as thick.
    /// </summary>
    public float HalfHeight = 20;

    /// <summary>
    /// Where the air is thickest, in metres.
    /// </summary>
    public float Height;

    /// <summary>
    /// What the air scatters light into: white for clean haze.
    /// </summary>
    public Color Color = new(1, 1, 1);

    /// <summary>
    /// How much the air scatters light onward rather than everywhere, -1 to 1: 0 evenly, towards 1 mostly ahead, as
    /// mist does.
    /// </summary>
    public float Anisotropy = 0.3f;

    public Fog() { }
}
