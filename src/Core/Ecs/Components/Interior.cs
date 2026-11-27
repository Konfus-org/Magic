namespace Magic.Ecs.Components;

/// <summary>
/// A box that is inside: a room, a cave or a sealed space, which light from outside does not spread into through its
/// walls.
/// </summary>
/// <remarks>
/// <para>
/// The box is a unit cube at the <see cref="Transform"/> of its entity, so its scale is its size in metres, as the
/// box of a <see cref="Physics"/> body without a model is. Fit it to the inner faces of the walls, floor and roof, or
/// to the middle of walls whose faces are not flat, such as logs.
/// </para>
/// <para>
/// Boxes that touch or overlap make one inside, so a room of another shape is a few boxes. The bounce light reads a
/// point in a box only from inside, and a point outside only from outside.
/// </para>
/// </remarks>
public record struct Interior : IComponent;
