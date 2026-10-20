using System.Numerics;
using Magic.Contexts.Components;
using Magic.Contexts.ECS;
using Magic.Contexts.Queries;

namespace Magic.Interfaces;

/// <summary>
/// Questions about the solid world, which a physics gem answers from its bodies as they stood after the last step:
/// what a ray hits, for picking what the pointer is over and for aiming, and what is within reach of a point. A body
/// is an entity with a <see cref="Transform"/> and a <see cref="Physics"/>; what one touched is on its
/// <see cref="Contacts"/>, not here. The main thread's, between steps: ask in Update, FixedUpdate or LateUpdate,
/// never in Render or on a worker, as the bodies move while the world steps.
/// </summary>
public interface IPhysics
{
    /// <summary>
    /// The nearest body of the kinds asked for that the segment from <paramref name="from"/> to <paramref name="to"/>
    /// hits, or none. A body the segment starts inside is not hit.
    /// </summary>
    RayHit? Cast(Vector3 from, Vector3 to, Motions motions = Motions.All);

    /// <summary>
    /// The bodies of the kinds asked for whose shapes reach within <paramref name="radius"/> of
    /// <paramref name="center"/>, written into <paramref name="found"/> in no order, as many as fit: how many were
    /// written.
    /// </summary>
    int Overlap(Vector3 center, float radius, Span<Entity> found, Motions motions = Motions.All);
}
