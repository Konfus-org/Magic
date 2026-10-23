using Magic.Contexts.ECS;
using Magic.Contexts.Frames;
using Magic.Interfaces;
using Magic.Samples.Assets.Components;
using Magic.Services;

namespace Magic.Samples.Assets.Scripts;

/// <summary>
/// Destroys every entity whose <see cref="Expires"/> time has come: a system on the main thread, as it changes which
/// entities there are, which a parallel system may not. It notes them as it goes over the query and destroys them after,
/// as entities go while nothing is going over them. A prefab attaches it to an entity of its own, <c>{ "id": 3439 }</c>,
/// and it takes no entity, as it runs over others.
/// </summary>
internal sealed class ExpireSystem(World world) : ISystemScript<Expires>
{
    private readonly List<Entity> _gone = [];

    public void Run(in Frame frame, EcsQuery<Expires> query)
    {
        foreach ((ReadOnlySpan<Entity> entities, Span<Expires> expiries) in query)
        {
            for (int i = 0; i < entities.Length; i++)
            {
                if (expiries[i].At <= frame.WorldSec) _gone.Add(entities[i]);
            }
        }

        foreach (Entity entity in _gone)
        {
            world.Destroy(entity);
        }

        _gone.Clear();
    }
}
