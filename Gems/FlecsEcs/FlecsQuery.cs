using Flecs.NET.Bindings;
using Flecs.NET.Core;
using Magic.Contexts;
using Magic.Interfaces;
using System.Runtime.CompilerServices;

namespace FlecsGem;

// Queries are cached: flecs keeps the list of matching tables up to date, so iterating never searches.
// Disabled entities live in their own tables which cached queries skip unless asked for Ecs.Disabled.
//
// Iteration drives the flecs iterator directly (GetIter/GetNext): nothing is allocated per call and no delegate
// sits between the caller's callback and the tables. Each() walks the chunk's columns by reference, so the
// caller's delegate is the only indirection per entity. The Flecs.NET builders are structs whose chaining
// methods mutate in place, so each With/Without is a statement.
//
// Cascade() turns the last component term into "the parent's, optional, parents first": flecs' cascade
// traversal over ChildOf. Its column is then shared by the chunk (see FlecsIter).

internal abstract unsafe class FlecsQueryBuilder(FlecsEcs ecs, int terms)
{
    protected QueryBuilder Builder = new QueryBuilder(ecs.Native.Handle).Cached();

    protected FlecsEcs Owner { get; } = ecs;

    /// <summary>
    /// Marks the last component term as read from the parent, breadth first, optional.
    /// </summary>
    protected void CascadeLast()
    {
        Builder.TermAt(terms - 1).Cascade(Ecs.ChildOf).Optional();
    }

    /// <summary>
    /// Adds "not T" on the entity or anything above it.
    /// </summary>
    protected void WithoutSelfOrParents<T>() where T : unmanaged
    {
        Builder.Without<T>().Self().Up(Ecs.ChildOf);
    }
}

internal abstract unsafe class FlecsQuery(FlecsEcs ecs, Query query) : IDisposable
{
    protected Query Query = query;

    protected FlecsEcs Owner { get; } = ecs;

    public void Dispose()
    {
        if (!Owner.IsDisposed)
            Query.Dispose();
    }

    public int Count() => Query.Count();

    protected flecs.ecs_iter_t Iter() => Query.GetIter(Owner.Native.Handle);
}

internal sealed class FlecsQueryBuilder<T1> : FlecsQueryBuilder, IEcsQueryBuilder<T1>
    where T1 : unmanaged
{
    public FlecsQueryBuilder(FlecsEcs ecs) : base(ecs, 1) { Builder.With<T1>(); }

    public IEcsQueryBuilder<T1> With<T>() where T : unmanaged { Builder.With<T>(); return this; }

    public IEcsQueryBuilder<T1> Without<T>() where T : unmanaged { Builder.Without<T>(); return this; }

    public IEcsQueryBuilder<T1> WithoutAbove<T>() where T : unmanaged { WithoutSelfOrParents<T>(); return this; }

    public IEcsQueryBuilder<T1> Cascade() { CascadeLast(); return this; }

    public IEcsQuery<T1> Build() => new FlecsQuery<T1>(Owner, Builder.Build());
}

internal sealed class FlecsQueryBuilder<T1, T2> : FlecsQueryBuilder, IEcsQueryBuilder<T1, T2>
    where T1 : unmanaged where T2 : unmanaged
{
    public FlecsQueryBuilder(FlecsEcs ecs) : base(ecs, 2) { Builder.With<T1>().With<T2>(); }

    public IEcsQueryBuilder<T1, T2> With<T>() where T : unmanaged { Builder.With<T>(); return this; }

    public IEcsQueryBuilder<T1, T2> Without<T>() where T : unmanaged { Builder.Without<T>(); return this; }

    public IEcsQueryBuilder<T1, T2> WithoutAbove<T>() where T : unmanaged { WithoutSelfOrParents<T>(); return this; }

    public IEcsQueryBuilder<T1, T2> Cascade() { CascadeLast(); return this; }

    public IEcsQuery<T1, T2> Build() => new FlecsQuery<T1, T2>(Owner, Builder.Build());
}

internal sealed class FlecsQueryBuilder<T1, T2, T3> : FlecsQueryBuilder, IEcsQueryBuilder<T1, T2, T3>
    where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged
{
    public FlecsQueryBuilder(FlecsEcs ecs) : base(ecs, 3) { Builder.With<T1>().With<T2>().With<T3>(); }

    public IEcsQueryBuilder<T1, T2, T3> With<T>() where T : unmanaged { Builder.With<T>(); return this; }

    public IEcsQueryBuilder<T1, T2, T3> Without<T>() where T : unmanaged { Builder.Without<T>(); return this; }

    public IEcsQueryBuilder<T1, T2, T3> WithoutAbove<T>() where T : unmanaged { WithoutSelfOrParents<T>(); return this; }

    public IEcsQueryBuilder<T1, T2, T3> Cascade() { CascadeLast(); return this; }

    public IEcsQuery<T1, T2, T3> Build() => new FlecsQuery<T1, T2, T3>(Owner, Builder.Build());
}

internal sealed class FlecsQueryBuilder<T1, T2, T3, T4> : FlecsQueryBuilder, IEcsQueryBuilder<T1, T2, T3, T4>
    where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged
{
    public FlecsQueryBuilder(FlecsEcs ecs) : base(ecs, 4) { Builder.With<T1>().With<T2>().With<T3>().With<T4>(); }

    public IEcsQueryBuilder<T1, T2, T3, T4> With<T>() where T : unmanaged { Builder.With<T>(); return this; }

    public IEcsQueryBuilder<T1, T2, T3, T4> Without<T>() where T : unmanaged { Builder.Without<T>(); return this; }

    public IEcsQueryBuilder<T1, T2, T3, T4> WithoutAbove<T>() where T : unmanaged { WithoutSelfOrParents<T>(); return this; }

    public IEcsQueryBuilder<T1, T2, T3, T4> Cascade() { CascadeLast(); return this; }

    public IEcsQuery<T1, T2, T3, T4> Build() => new FlecsQuery<T1, T2, T3, T4>(Owner, Builder.Build());
}

internal sealed unsafe class FlecsQuery<T1>(FlecsEcs ecs, Query query) : FlecsQuery(ecs, query), IEcsQuery<T1>
    where T1 : unmanaged
{
    public void Each(QueryEachAction<T1> action)
    {
        flecs.ecs_iter_t it = Iter();
        while (Query.GetNext(&it))
        {
            ref T1 c1 = ref FlecsIter.First<T1>(&it, 0, out int s1);

            for (int i = 0, count = it.count; i < count; i++)
                action(new Handle(it.entities[i]), ref Unsafe.Add(ref c1, i * s1));
        }
    }

    public void Run(QueryChunkAction<T1> action)
    {
        flecs.ecs_iter_t it = Iter();
        while (Query.GetNext(&it))
            action(FlecsIter.Entities(&it), FlecsIter.Column<T1>(&it, 0));
    }
}

internal sealed unsafe class FlecsQuery<T1, T2>(FlecsEcs ecs, Query query) : FlecsQuery(ecs, query), IEcsQuery<T1, T2>
    where T1 : unmanaged where T2 : unmanaged
{
    public void Each(QueryEachAction<T1, T2> action)
    {
        flecs.ecs_iter_t it = Iter();
        while (Query.GetNext(&it))
        {
            ref T1 c1 = ref FlecsIter.First<T1>(&it, 0, out int s1);
            ref T2 c2 = ref FlecsIter.First<T2>(&it, 1, out int s2);

            for (int i = 0, count = it.count; i < count; i++)
                action(new Handle(it.entities[i]), ref Unsafe.Add(ref c1, i * s1), ref Unsafe.Add(ref c2, i * s2));
        }
    }

    public void Run(QueryChunkAction<T1, T2> action)
    {
        flecs.ecs_iter_t it = Iter();
        while (Query.GetNext(&it))
            action(FlecsIter.Entities(&it), FlecsIter.Column<T1>(&it, 0), FlecsIter.Column<T2>(&it, 1));
    }
}

internal sealed unsafe class FlecsQuery<T1, T2, T3>(FlecsEcs ecs, Query query) : FlecsQuery(ecs, query), IEcsQuery<T1, T2, T3>
    where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged
{
    public void Each(QueryEachAction<T1, T2, T3> action)
    {
        flecs.ecs_iter_t it = Iter();
        while (Query.GetNext(&it))
        {
            ref T1 c1 = ref FlecsIter.First<T1>(&it, 0, out int s1);
            ref T2 c2 = ref FlecsIter.First<T2>(&it, 1, out int s2);
            ref T3 c3 = ref FlecsIter.First<T3>(&it, 2, out int s3);

            for (int i = 0, count = it.count; i < count; i++)
            {
                action(
                    new Handle(it.entities[i]),
                    ref Unsafe.Add(ref c1, i * s1),
                    ref Unsafe.Add(ref c2, i * s2),
                    ref Unsafe.Add(ref c3, i * s3));
            }
        }
    }

    public void Run(QueryChunkAction<T1, T2, T3> action)
    {
        flecs.ecs_iter_t it = Iter();
        while (Query.GetNext(&it))
        {
            action(
                FlecsIter.Entities(&it),
                FlecsIter.Column<T1>(&it, 0),
                FlecsIter.Column<T2>(&it, 1),
                FlecsIter.Column<T3>(&it, 2));
        }
    }
}

internal sealed unsafe class FlecsQuery<T1, T2, T3, T4>(FlecsEcs ecs, Query query) : FlecsQuery(ecs, query), IEcsQuery<T1, T2, T3, T4>
    where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged
{
    public void Each(QueryEachAction<T1, T2, T3, T4> action)
    {
        flecs.ecs_iter_t it = Iter();
        while (Query.GetNext(&it))
        {
            ref T1 c1 = ref FlecsIter.First<T1>(&it, 0, out int s1);
            ref T2 c2 = ref FlecsIter.First<T2>(&it, 1, out int s2);
            ref T3 c3 = ref FlecsIter.First<T3>(&it, 2, out int s3);
            ref T4 c4 = ref FlecsIter.First<T4>(&it, 3, out int s4);

            for (int i = 0, count = it.count; i < count; i++)
            {
                action(
                    new Handle(it.entities[i]),
                    ref Unsafe.Add(ref c1, i * s1),
                    ref Unsafe.Add(ref c2, i * s2),
                    ref Unsafe.Add(ref c3, i * s3),
                    ref Unsafe.Add(ref c4, i * s4));
            }
        }
    }

    public void Run(QueryChunkAction<T1, T2, T3, T4> action)
    {
        flecs.ecs_iter_t it = Iter();
        while (Query.GetNext(&it))
        {
            action(
                FlecsIter.Entities(&it),
                FlecsIter.Column<T1>(&it, 0),
                FlecsIter.Column<T2>(&it, 1),
                FlecsIter.Column<T3>(&it, 2),
                FlecsIter.Column<T4>(&it, 3));
        }
    }
}
