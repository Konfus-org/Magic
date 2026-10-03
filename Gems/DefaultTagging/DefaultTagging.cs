using Magic.Interfaces;
using Magic.Services;

namespace DefaultTaggingGem;

/// <summary>
/// The gem: keeps the markers the well-known tags stand for (<see cref="Magic.Contexts.Components.Static"/>,
/// <see cref="Magic.Contexts.Components.Hidden"/>) on every entity whose <see cref="Magic.Contexts.Components.Tags"/>
/// carry them, one system on the LateUpdate phase. It loads before the WorldTransforms gem, so a static entity is
/// marked before its first world transform is computed.
/// </summary>
internal sealed class DefaultTagging : IGem
{
    private readonly TagSystem _system;
    private readonly IDisposable _scheduled;

    public DefaultTagging(IEcs ecs, Scheduler scheduler)
    {
        _system = new TagSystem(ecs);
        _scheduled = scheduler.Add(ecs, _system);
    }

    public void Dispose()
    {
        _scheduled.Dispose();
        _system.Dispose();
    }
}
