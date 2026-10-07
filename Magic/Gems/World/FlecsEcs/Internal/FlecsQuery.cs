using Flecs.NET.Bindings;
using Flecs.NET.Core;
using Magic.Contexts;
using Magic.Interfaces;
using System.Runtime.CompilerServices;

namespace FlecsEcsGem;

// Queries are cached: flecs keeps the list of matching tables up to date, so iterating never searches.
// Disabled entities live in their own tables which cached queries skip unless asked for Ecs.Disabled.
//
// Iteration drives the flecs iterator directly (GetIter/GetNext): nothing is allocated per call and no delegate
// sits between the caller's callback and the tables. Each() walks the chunk's columns by reference, so the
// caller's delegate is the only indirection per entity. The Flecs.NET builders are structs whose chaining
// methods mutate in place, so each With/Without is a statement.

internal abstract unsafe class FlecsQueryBuilder(FlecsEcs ecs)
{
    protected QueryBuilder Builder = new QueryBuilder(ecs.Native.Handle).Cached();

    protected FlecsEcs Owner { get; } = ecs;
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
    public FlecsQueryBuilder(FlecsEcs ecs) : base(ecs) { Builder.With<T1>(); }

    public IEcsQueryBuilder<T1> With<T>() where T : unmanaged { Builder.With<T>(); return this; }

    public IEcsQueryBuilder<T1> Without<T>() where T : unmanaged { Builder.Without<T>(); return this; }

    public IEcsQuery<T1> Build() => new FlecsQuery<T1>(Owner, Builder.Build());
}

internal sealed class FlecsQueryBuilder<T1, T2> : FlecsQueryBuilder, IEcsQueryBuilder<T1, T2>
    where T1 : unmanaged where T2 : unmanaged
{
    public FlecsQueryBuilder(FlecsEcs ecs) : base(ecs) { Builder.With<T1>().With<T2>(); }

    public IEcsQueryBuilder<T1, T2> With<T>() where T : unmanaged { Builder.With<T>(); return this; }

    public IEcsQueryBuilder<T1, T2> Without<T>() where T : unmanaged { Builder.Without<T>(); return this; }

    public IEcsQuery<T1, T2> Build() => new FlecsQuery<T1, T2>(Owner, Builder.Build());
}

internal sealed unsafe class FlecsQuery<T1>(FlecsEcs ecs, Query query) : FlecsQuery(ecs, query), IEcsQuery<T1>
    where T1 : unmanaged
{
    public void Each(QueryEachAction<T1> action)
    {
        flecs.ecs_iter_t it = Iter();
        while (Query.GetNext(&it))
        {
            ref T1 c1 = ref FlecsIter.First<T1>(&it, 0);

            for (int i = 0, count = it.count; i < count; i++)
                action(new Handle(it.entities[i]), ref Unsafe.Add(ref c1, i));
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
            ref T1 c1 = ref FlecsIter.First<T1>(&it, 0);
            ref T2 c2 = ref FlecsIter.First<T2>(&it, 1);

            for (int i = 0, count = it.count; i < count; i++)
                action(new Handle(it.entities[i]), ref Unsafe.Add(ref c1, i), ref Unsafe.Add(ref c2, i));
        }
    }

    public void Run(QueryChunkAction<T1, T2> action)
    {
        flecs.ecs_iter_t it = Iter();
        while (Query.GetNext(&it))
            action(FlecsIter.Entities(&it), FlecsIter.Column<T1>(&it, 0), FlecsIter.Column<T2>(&it, 1));
    }
}
