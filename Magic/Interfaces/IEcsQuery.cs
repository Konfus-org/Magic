using Magic.Contexts;

namespace Magic.Interfaces;

// A query matches every enabled entity that has all of its component type parameters (those are handed to the
// callbacks) plus any With<T>() tags, and none of the Without<T>() ones. Build it once and keep it: building is
// the expensive part, iterating is not.
//
// Each(...) calls back once per entity. Run(...) calls back once per chunk (a run of entities stored together)
// with the entities and their component columns as spans straight over storage: no copy, one call per chunk,
// which is the fastest way to touch a lot of entities.
//
// Cascade(): the last component type parameter is read from the entity's parent instead of the entity itself,
// and chunks come parents before children. A chunk of root entities gets an empty span for it; every other
// chunk gets a one-element span, the shared value of the parent all its entities have. In Each(...) an absent
// parent reads as default. Mind that a cached query with it is re-matched over every table whenever the hierarchy or
// the parent's component changes: the transform system, which it was made for, reads parents by hand instead and
// repeats its pass until the order no longer matters, because the re-match stalled a streaming world every spawn.
//
// WithoutAbove<T>(): Without<T>() that also looks up the hierarchy: neither the entity nor anything above it has
// T. It is decided when a T is added or removed, not per entity while iterating.

public interface IEcsQueryBuilder<T1> where T1 : unmanaged
{
    IEcsQueryBuilder<T1> With<T>() where T : unmanaged;

    IEcsQueryBuilder<T1> Without<T>() where T : unmanaged;

    IEcsQueryBuilder<T1> WithoutAbove<T>() where T : unmanaged;

    IEcsQueryBuilder<T1> Cascade();

    IEcsQuery<T1> Build();
}

public interface IEcsQueryBuilder<T1, T2> where T1 : unmanaged where T2 : unmanaged
{
    IEcsQueryBuilder<T1, T2> With<T>() where T : unmanaged;

    IEcsQueryBuilder<T1, T2> Without<T>() where T : unmanaged;

    IEcsQueryBuilder<T1, T2> WithoutAbove<T>() where T : unmanaged;

    IEcsQueryBuilder<T1, T2> Cascade();

    IEcsQuery<T1, T2> Build();
}

public interface IEcsQueryBuilder<T1, T2, T3> where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged
{
    IEcsQueryBuilder<T1, T2, T3> With<T>() where T : unmanaged;

    IEcsQueryBuilder<T1, T2, T3> Without<T>() where T : unmanaged;

    IEcsQueryBuilder<T1, T2, T3> WithoutAbove<T>() where T : unmanaged;

    IEcsQueryBuilder<T1, T2, T3> Cascade();

    IEcsQuery<T1, T2, T3> Build();
}

public interface IEcsQueryBuilder<T1, T2, T3, T4> where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged
{
    IEcsQueryBuilder<T1, T2, T3, T4> With<T>() where T : unmanaged;

    IEcsQueryBuilder<T1, T2, T3, T4> Without<T>() where T : unmanaged;

    IEcsQueryBuilder<T1, T2, T3, T4> WithoutAbove<T>() where T : unmanaged;

    IEcsQueryBuilder<T1, T2, T3, T4> Cascade();

    IEcsQuery<T1, T2, T3, T4> Build();
}

/// <summary>
/// A built query. Dispose it when its owner (usually a gem) goes away.
/// </summary>
public interface IEcsQuery<T1> : IDisposable where T1 : unmanaged
{
    void Each(QueryEachAction<T1> action);

    void Run(QueryChunkAction<T1> action);

    int Count();
}

public interface IEcsQuery<T1, T2> : IDisposable where T1 : unmanaged where T2 : unmanaged
{
    void Each(QueryEachAction<T1, T2> action);

    void Run(QueryChunkAction<T1, T2> action);

    int Count();
}

public interface IEcsQuery<T1, T2, T3> : IDisposable where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged
{
    void Each(QueryEachAction<T1, T2, T3> action);

    void Run(QueryChunkAction<T1, T2, T3> action);

    int Count();
}

public interface IEcsQuery<T1, T2, T3, T4> : IDisposable where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged
{
    void Each(QueryEachAction<T1, T2, T3, T4> action);

    void Run(QueryChunkAction<T1, T2, T3, T4> action);

    int Count();
}

public delegate void QueryEachAction<T1>(Handle entity, ref T1 c1)
    where T1 : unmanaged;

public delegate void QueryEachAction<T1, T2>(Handle entity, ref T1 c1, ref T2 c2)
    where T1 : unmanaged where T2 : unmanaged;

public delegate void QueryEachAction<T1, T2, T3>(Handle entity, ref T1 c1, ref T2 c2, ref T3 c3)
    where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged;

public delegate void QueryEachAction<T1, T2, T3, T4>(Handle entity, ref T1 c1, ref T2 c2, ref T3 c3, ref T4 c4)
    where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged;

public delegate void QueryChunkAction<T1>(ReadOnlySpan<Handle> entities, Span<T1> c1)
    where T1 : unmanaged;

public delegate void QueryChunkAction<T1, T2>(ReadOnlySpan<Handle> entities, Span<T1> c1, Span<T2> c2)
    where T1 : unmanaged where T2 : unmanaged;

public delegate void QueryChunkAction<T1, T2, T3>(ReadOnlySpan<Handle> entities, Span<T1> c1, Span<T2> c2, Span<T3> c3)
    where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged;

public delegate void QueryChunkAction<T1, T2, T3, T4>(ReadOnlySpan<Handle> entities, Span<T1> c1, Span<T2> c2, Span<T3> c3, Span<T4> c4)
    where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged;
