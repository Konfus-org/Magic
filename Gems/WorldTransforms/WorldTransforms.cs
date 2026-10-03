using Magic.Interfaces;
using Magic.Services;

namespace WorldTransformsGem;

/// <summary>
/// The gem: the transform hierarchy, one system on the LateUpdate phase, after gameplay has moved things and before
/// the renderer reads them. It settles entities carrying the <see cref="Magic.Contexts.Components.Static"/> marker,
/// which the DefaultTagging gem keeps; without that gem nothing settles and every entity is computed each frame.
/// </summary>
internal sealed class WorldTransforms : IGem
{
    private readonly TransformSystem _system;
    private readonly IDisposable _scheduled;

    public WorldTransforms(IEcs ecs, Scheduler scheduler)
    {
        _system = new TransformSystem(ecs);
        _scheduled = scheduler.Add(ecs, _system);
    }

    public void Dispose()
    {
        _scheduled.Dispose();
        _system.Dispose();
    }
}
