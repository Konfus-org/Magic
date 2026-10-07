using Magic.Contexts;

namespace Magic.Interfaces;

/// <summary>
/// A query matches every enabled entity that has all of its component type parameters (those are handed to the
/// callbacks) plus any <c>With&lt;T&gt;()</c> tags, and none of the <c>Without&lt;T&gt;()</c> ones. Build it once and
/// keep it: building is the expensive part, iterating is not.
/// <para>
/// <c>Each(...)</c> calls back once per entity. <c>Run(...)</c> calls back once per chunk (a run of entities stored
/// together) with the entities and their component columns as spans straight over storage: no copy, one call per
/// chunk, which is the fastest way to touch a lot of entities.
/// </para>
/// </summary>
public interface IEcsQueryBuilder<T1> where T1 : unmanaged
{
    IEcsQueryBuilder<T1> With<T>() where T : unmanaged;

    IEcsQueryBuilder<T1> Without<T>() where T : unmanaged;

    IEcsQuery<T1> Build();
}

/// <summary>
/// <see cref="IEcsQueryBuilder{T1}"/> for two components.
/// </summary>
public interface IEcsQueryBuilder<T1, T2> where T1 : unmanaged where T2 : unmanaged
{
    IEcsQueryBuilder<T1, T2> With<T>() where T : unmanaged;

    IEcsQueryBuilder<T1, T2> Without<T>() where T : unmanaged;

    IEcsQuery<T1, T2> Build();
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

/// <summary>
/// <see cref="IEcsQuery{T1}"/> for two components.
/// </summary>
public interface IEcsQuery<T1, T2> : IDisposable where T1 : unmanaged where T2 : unmanaged
{
    void Each(QueryEachAction<T1, T2> action);

    void Run(QueryChunkAction<T1, T2> action);

    int Count();
}

public delegate void QueryEachAction<T1>(Handle entity, ref T1 c1)
    where T1 : unmanaged;

public delegate void QueryEachAction<T1, T2>(Handle entity, ref T1 c1, ref T2 c2)
    where T1 : unmanaged where T2 : unmanaged;

public delegate void QueryChunkAction<T1>(ReadOnlySpan<Handle> entities, Span<T1> c1)
    where T1 : unmanaged;

public delegate void QueryChunkAction<T1, T2>(ReadOnlySpan<Handle> entities, Span<T1> c1, Span<T2> c2)
    where T1 : unmanaged where T2 : unmanaged;
