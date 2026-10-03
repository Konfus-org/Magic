using Magic.Contexts;
using Magic.Contexts.Components;
using Magic.Interfaces;

namespace Magic.Systems;

/// <summary>
/// Keeps the markers under the well-known tags: an entity whose <see cref="Tags"/> hold <see cref="Tag.Static"/> carries
/// <see cref="Static"/>, one with <see cref="Tag.Hidden"/> carries <see cref="Hidden"/>, and one without loses it. The
/// tags are what is written (in a chunk file, or by setting <see cref="Tags"/>); the markers are what the systems
/// query, since a query can filter on a component and look up the hierarchy for one, which it cannot do for a tag
/// inside the <see cref="Tags"/> container. Runs in LateUpdate before the transform system, so tags set during the
/// frame take effect in it. Setting <see cref="Tags"/> is what is seen; editing them in place through a reference is
/// not. The streaming system puts the markers on with the tags when it spawns a chunk, so nothing moves twice.
/// </summary>
internal sealed class TagSystem : ISystem
{
    private readonly IEcs _ecs;
    private readonly IDisposable _retagged;
    private readonly List<Handle> _changed = []; // the observer must not change the world: applied in Run

    public TagSystem(IEcs ecs)
    {
        _ecs = ecs;
        _retagged = ecs.Observe<Tags>(ComponentEvent.Set, entity =>
        {
            if (!Matches(entity))
                _changed.Add(entity);
        });
    }

    public void Dispose()
    {
        _retagged.Dispose();
    }

    public UpdateType Phase => UpdateType.LateUpdate;

    public void Run(in Frame frame)
    {
        foreach (Handle entity in _changed)
        {
            if (!_ecs.IsAlive(entity) || !_ecs.TryGet<Tags>(entity, out Tags tags))
                continue;

            Mark<Static>(entity, tags.Has(Tag.Static));
            Mark<Hidden>(entity, tags.Has(Tag.Hidden));
        }

        _changed.Clear();
    }

    /// <summary>
    /// Whether the entity's markers already say what its tags say.
    /// </summary>
    private bool Matches(Handle entity)
    {
        return _ecs.TryGet<Tags>(entity, out Tags tags)
            && _ecs.Has<Static>(entity) == tags.Has(Tag.Static)
            && _ecs.Has<Hidden>(entity) == tags.Has(Tag.Hidden);
    }

    private void Mark<T>(Handle entity, bool wanted) where T : unmanaged
    {
        bool has = _ecs.Has<T>(entity);
        if (wanted && !has)
            _ecs.Add<T>(entity);
        else if (!wanted && has)
            _ecs.Remove<T>(entity);
    }
}
