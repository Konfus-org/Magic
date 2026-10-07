using Magic.Interfaces;
using Magic.Utils;

namespace EntityHierarchyGem;

/// <summary>
/// The gem: the entity tree's markers and world transforms (<see cref="HierarchySystem"/>), one system on the
/// LateUpdate phase. The markers (<see cref="Magic.Contexts.Components.Static"/>, <see cref="Magic.Contexts.Components.Hidden"/>,
/// <see cref="Magic.Contexts.Components.Settled"/>) are its types: a gem that queries them references this one.
/// </summary>
internal sealed class EntityHierarchy : IGem
{
    private readonly HierarchySystem _system;
    private readonly Subscription _scheduled;

    public EntityHierarchy(IEcs ecs, ITelemetry telemetry)
    {
        _system = new HierarchySystem(ecs, telemetry);
        _scheduled = ecs.Add(_system);
    }

    public void Dispose()
    {
        _scheduled.Dispose();
        _system.Dispose();
    }
}
