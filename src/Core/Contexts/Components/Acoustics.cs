using Magic.Interfaces;

namespace Magic.Contexts.Components;

/// <summary>
/// What a solid thing is made of, as sound hears it: how much of a sound it soaks up, how it scatters the rest and how
/// much passes through it. Beside a <see cref="Physics"/>, which makes the entity solid; a solid without one sounds
/// generic. In a file: <c>"Acoustics": { "material": "carpet" }</c>.
/// </summary>
public record struct Acoustics : IComponent
{
    public AcousticMaterial Material { get; set; }
}

/// <summary>
/// The materials an <see cref="Acoustics"/> can be: measured ones, from hard and ringing to soft and dead.
/// </summary>
public enum AcousticMaterial : byte
{
    /// <summary>
    /// Neither hard nor soft: what a solid sounds like when nothing says.
    /// </summary>
    Generic,

    Brick,

    Concrete,

    Ceramic,

    Gravel,

    /// <summary>
    /// Soaks up most of what reaches it, the high notes most: a room of it sounds dead.
    /// </summary>
    Carpet,

    /// <summary>
    /// Reflects most of what reaches it and lets a little through.
    /// </summary>
    Glass,

    Plaster,

    Wood,

    Metal,

    Rock,
}
