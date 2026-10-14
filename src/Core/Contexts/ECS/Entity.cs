using Magic.Interfaces;

namespace Magic.Contexts.ECS;

/// <summary>
/// An entity: a handle to the components <see cref="IEcs"/> holds for it. Three ints in this order, as the ECS gem
/// lays out its own, so a query hands out the gem's entity arrays without copying them.
/// </summary>
/// <param name="Id">Its slot, which a later entity reuses once it is destroyed.</param>
/// <param name="EcsWorld">Which of the ECS gem's own worlds it lives in, kept for its layout; the game's world is
/// <see cref="Services.World"/>.</param>
/// <param name="Version">How many entities have had its slot, so a handle to a destroyed entity is never taken for
/// the one in its slot now.</param>
public readonly record struct Entity(int Id, int EcsWorld, int Version);
