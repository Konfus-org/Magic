using Magic.Contexts;
using Magic.Attributes.Scripts;
using Magic.Contexts.Components;
using Magic.Interfaces;
using Magic.Utils;
using System.Numerics;

namespace WorldTransformsGem;

/// <summary>
/// Keeps every entity's <see cref="WorldTransform"/> equal to its <see cref="Transform"/> composed with its
/// parents'. It runs in LateUpdate, once gameplay has moved things and before the renderer reads them. An entity tagged <see cref="Tag.Static"/> (the <see cref="Static"/> marker the tag system keeps under
/// it) is computed once, the first time it is seen, then marked <see cref="Settled"/> so no query walks it again;
/// setting its <see cref="Tags"/> (re-adding the tag after moving it) clears the mark and it is computed once more.
/// Editing the tags in place through a reference does not. Host owned, engine agnostic: it only uses <see cref="IEcs"/>.
///
/// Parents are read by hand, once per chunk of entities (one table: one parent), not through a cascading query: the
/// ECS re-matches a cached query that looks up the hierarchy over every table each time a chunk spawns, a stall of
/// many milliseconds while streaming. Order comes from repeating instead: a pass composes every entity with its
/// parent's world as it is now, and passes go on until one changes nothing, so a child computed before its parent is
/// put right by the next pass. That is a pass per level of entities that moved, at most <see cref="MaxPasses"/>.
/// </summary>
[Phase(UpdateType.LateUpdate)]
internal sealed class TransformSystem : ISystem
{
    /// <summary>
    /// The most passes a frame makes over what moves: a chain of moving entities deeper than this is a frame behind
    /// at the bottom. Nothing in the engine nests moving things that deep; a statics' first computation is the same.
    /// </summary>
    private const int MaxPasses = 8;

    private readonly IEcs _ecs;
    private readonly IEcsQuery<Transform> _missing;
    private readonly IEcsQuery<Transform, WorldTransform> _moving;
    private readonly IEcsQuery<Transform, WorldTransform> _settling;
    private readonly QueryChunkAction<Transform, WorldTransform> _compute; // one delegate each, not one per frame
    private readonly QueryChunkAction<Transform, WorldTransform> _computeAndSettle;
    private readonly IDisposable _retagged;
    private readonly List<Handle> _missingWorld = [];
    private readonly List<Handle> _settled = []; // statics computed this pass, marked after it
    private readonly List<Handle> _unsettled = []; // settled entities whose tags were set since the last pass
    private bool _changed; // the pass under way changed a world transform
    private long _lastSlowLog;

    public TransformSystem(IEcs ecs)
    {
        _ecs = ecs;
        _missing = ecs.Query<Transform>().Without<WorldTransform>().Build();
        _moving = ecs.Query<Transform, WorldTransform>().Without<Static>().Build();
        _settling = ecs.Query<Transform, WorldTransform>().With<Static>().Without<Settled>().Build();
        _compute = Compute;
        _computeAndSettle = ComputeAndSettle;
        _retagged = ecs.Observe<Tags>(ComponentEvent.Set, entity =>
        {
            if (_ecs.Has<Settled>(entity))
                _unsettled.Add(entity);
        });
    }

    public void Dispose()
    {
        _missing.Dispose();
        _moving.Dispose();
        _settling.Dispose();
        _retagged.Dispose();
    }

    /// <summary>
    /// One pass: gives new entities a <see cref="WorldTransform"/>, then recomputes every one that can change.
    /// </summary>
    public void Run(in Frame frame)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        ComputeWorlds();
        double lastMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        Debugging.Stats.Set("Transforms.Ms", lastMs);

        if (lastMs > 20f && Environment.TickCount64 - _lastSlowLog > 5000)
        {
            _lastSlowLog = Environment.TickCount64;
            Debugging.Log.Verbose($"Transform system took {lastMs:F1} ms.");
        }
    }

    private void ComputeWorlds()
    {
        // Structural changes cannot happen inside the observer, so the marks come off here.
        foreach (Handle entity in _unsettled)
        {
            if (_ecs.IsAlive(entity))
                _ecs.Remove<Settled>(entity);
        }

        _unsettled.Clear();

        if (_missing.Count() > 0)
        {
            // Collect first: adding a component is a structural change and cannot happen inside the iteration.
            _missingWorld.Clear();
            _missing.Run((ReadOnlySpan<Handle> entities, Span<Transform> _) => _missingWorld.AddRange(entities));
            foreach (Handle entity in _missingWorld)
                _ecs.Add<WorldTransform>(entity);
        }

        Converge(_moving, _compute);
        Converge(_settling, _computeAndSettle);
        foreach (Handle entity in _settled)
            _ecs.Add<Settled>(entity);
    }

    /// <summary>
    /// Passes over the query until one changes nothing (or <see cref="MaxPasses"/> have run): each pass puts right
    /// what the one before composed with a parent that had not been computed yet.
    /// </summary>
    private void Converge(IEcsQuery<Transform, WorldTransform> query, QueryChunkAction<Transform, WorldTransform> compute)
    {
        for (int pass = 0; pass < MaxPasses; pass++)
        {
            _changed = false;
            _settled.Clear();
            query.Run(compute);
            if (!_changed)
                return;
        }
    }

    private void Compute(ReadOnlySpan<Handle> entities, Span<Transform> local, Span<WorldTransform> world)
    {
        Matrix4x4 parentWorld = ParentWorld(entities);
        for (int i = 0; i < local.Length; i++)
        {
            Matrix4x4 composed = local[i].Matrix * parentWorld;
            if (composed == world[i].Value)
                continue;

            world[i].Value = composed;
            _changed = true;
        }
    }

    /// <summary>
    /// A static entity's first and only computation; it is marked once the passes agree on it.
    /// </summary>
    private void ComputeAndSettle(ReadOnlySpan<Handle> entities, Span<Transform> local, Span<WorldTransform> world)
    {
        Compute(entities, local, world);
        _settled.AddRange(entities);
    }

    /// <summary>
    /// The world transform of the parent the entities share (one table, one parent): identity for roots and for a
    /// parent that has no place in the world of its own.
    /// </summary>
    private Matrix4x4 ParentWorld(ReadOnlySpan<Handle> entities)
    {
        if (entities.IsEmpty)
            return Matrix4x4.Identity;

        Handle parent = _ecs.GetParent(entities[0]);
        return parent.IsValid && _ecs.TryGet<WorldTransform>(parent, out WorldTransform world) ? world.Value : Matrix4x4.Identity;
    }
}
