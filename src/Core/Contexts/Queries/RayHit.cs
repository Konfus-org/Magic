using System.Numerics;
using Magic.Contexts.ECS;

namespace Magic.Contexts.Queries;

/// <summary>
/// Where a ray met a body: the entity, the point and the surface's normal there, in the world, and metres from the
/// ray's start.
/// </summary>
public readonly record struct RayHit(Entity Entity, Vector3 Point, Vector3 Normal, float Distance);
