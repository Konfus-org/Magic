namespace Magic.Assets.Types;

/// <summary>
/// What a <see cref="Material"/> sounds like, as a measured material, from hard and ringing to soft and dead: how much
/// of a sound it soaks up, how it scatters the rest and how much passes through it. A solid with none sounds generic.
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
