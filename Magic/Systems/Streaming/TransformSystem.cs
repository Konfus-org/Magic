using Magic.Contexts;
using Magic.Contexts.Components;
using Magic.Interfaces;
using Magic.Utils;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Magic.Systems.Streaming;

/// <summary>
/// Keeps every entity's <see cref="WorldTransform"/> equal to its <see cref="Transform"/> composed with its
/// parents'. It runs in LateUpdate, once gameplay has moved things and before the renderer reads them. An entity tagged <see cref="Tag.Static"/> is computed once, the first time it is
/// seen, then marked <see cref="Settled"/> so no query walks it again; setting its <see cref="Tags"/> (re-adding
/// the tag after moving it) clears the mark and it is computed once more. Editing the tags in place through a
/// reference does not. Host owned, engine agnostic: it only uses <see cref="IEcs"/>.
/// </summary>
internal sealed class TransformSystem : ISystem
{
    private readonly IEcs _ecs;
    private readonly IEcsQuery<Transform> _missing;
    private readonly IEcsQuery<Transform, WorldTransform, WorldTransform> _untagged;
    private readonly IEcsQuery<Transform, WorldTransform, Tags, WorldTransform> _tagged;
    private readonly QueryChunkAction<Transform, WorldTransform, Tags, WorldTransform> _computeTagged; // one delegate, not one per frame
    private readonly IDisposable _retagged;
    private readonly List<Handle> _missingWorld = [];
    private readonly List<Handle> _settled = []; // statics computed this pass, marked after it
    private readonly List<Handle> _unsettled = []; // settled entities whose tags were set since the last pass
    private long _lastSlowLog;

    public TransformSystem(IEcs ecs)
    {
        _ecs = ecs;
        _missing = ecs.Query<Transform>().Without<WorldTransform>().Build();
        _untagged = ecs.Query<Transform, WorldTransform, WorldTransform>().Without<Tags>().Cascade().Build();
        _tagged = ecs.Query<Transform, WorldTransform, Tags, WorldTransform>().Without<Settled>().Cascade().Build();
        _computeTagged = ComputeTagged;
        _retagged = ecs.Observe<Tags>(ComponentEvent.Set, entity =>
        {
            if (_ecs.Has<Settled>(entity))
                _unsettled.Add(entity);
        });
    }

    public void Dispose()
    {
        _missing.Dispose();
        _untagged.Dispose();
        _tagged.Dispose();
        _retagged.Dispose();
    }

    public UpdateType Phase => UpdateType.LateUpdate;

    /// <summary>Milliseconds the last pass took.</summary>
    public float LastMs { get; private set; }

    /// <summary>One pass: gives new entities a <see cref="WorldTransform"/>, then recomputes every one that can change.</summary>
    public void Run(in Frame frame)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        ComputeWorlds();
        LastMs = (float)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        if (LastMs > 20f && Environment.TickCount64 - _lastSlowLog > 5000)
        {
            _lastSlowLog = Environment.TickCount64;
            Debugging.Log.Verbose($"Transform system took {LastMs:F1} ms.");
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

        _untagged.Run(static (ReadOnlySpan<Handle> _, Span<Transform> local, Span<WorldTransform> world, Span<WorldTransform> parent) =>
        {
            if (parent.IsEmpty)
            {
                for (int i = 0; i < local.Length; i++)
                    world[i].Value = local[i].Matrix;

                return;
            }

            Matrix4x4 parentWorld = parent[0].Value;
            for (int i = 0; i < local.Length; i++)
                world[i].Value = local[i].Matrix * parentWorld;
        });

        _settled.Clear();
        _tagged.Run(_computeTagged);
        foreach (Handle entity in _settled)
            _ecs.Add<Settled>(entity);
    }

    private void ComputeTagged(ReadOnlySpan<Handle> entities, Span<Transform> local, Span<WorldTransform> world, Span<Tags> tags, Span<WorldTransform> parent)
    {
        Matrix4x4 parentWorld = parent.IsEmpty ? Matrix4x4.Identity : parent[0].Value;
        for (int i = 0; i < local.Length; i++)
        {
            world[i].Value = parent.IsEmpty ? local[i].Matrix : local[i].Matrix * parentWorld;
            if (tags[i].Has(Tag.Static))
                _settled.Add(entities[i]);
        }
    }

    /// <summary>
    /// On a static entity whose world transform has been computed. Being a component rather than a check moves
    /// settled statics into their own tables, which <c>_tagged</c> excludes, so they cost nothing per frame.
    /// </summary>
    private struct Settled;
}
