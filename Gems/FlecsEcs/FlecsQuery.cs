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

internal abstract unsafe class FlecsQueryBuilder(FlecsWorld world)
{
    protected FlecsWorld World { get; } = world;
    protected QueryBuilder Builder = new QueryBuilder(world.Native.Handle).Cached();
}

internal abstract unsafe class FlecsQuery(FlecsWorld world, Query query) : IDisposable
{
    protected FlecsWorld World { get; } = world;
    protected Query Query = query;

    public int Count() => Query.Count();

    public void Dispose()
    {
        if (!World.IsDisposed)
            Query.Dispose();
    }

    protected flecs.ecs_iter_t Iter() => Query.GetIter(World.Native.Handle);
}

internal sealed class FlecsQueryBuilder<T1> : FlecsQueryBuilder, IWorldQueryBuilder<T1>
    where T1 : unmanaged
{
    public FlecsQueryBuilder(FlecsWorld world) : base(world) { Builder.With<T1>(); }

    public IWorldQueryBuilder<T1> With<T>() where T : unmanaged { Builder.With<T>(); return this; }
    public IWorldQueryBuilder<T1> Without<T>() where T : unmanaged { Builder.Without<T>(); return this; }
    public IWorldQuery<T1> Build() => new FlecsQuery<T1>(World, Builder.Build());
}

internal sealed class FlecsQueryBuilder<T1, T2> : FlecsQueryBuilder, IWorldQueryBuilder<T1, T2>
    where T1 : unmanaged where T2 : unmanaged
{
    public FlecsQueryBuilder(FlecsWorld world) : base(world) { Builder.With<T1>().With<T2>(); }

    public IWorldQueryBuilder<T1, T2> With<T>() where T : unmanaged { Builder.With<T>(); return this; }
    public IWorldQueryBuilder<T1, T2> Without<T>() where T : unmanaged { Builder.Without<T>(); return this; }
    public IWorldQuery<T1, T2> Build() => new FlecsQuery<T1, T2>(World, Builder.Build());
}

internal sealed class FlecsQueryBuilder<T1, T2, T3> : FlecsQueryBuilder, IWorldQueryBuilder<T1, T2, T3>
    where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged
{
    public FlecsQueryBuilder(FlecsWorld world) : base(world) { Builder.With<T1>().With<T2>().With<T3>(); }

    public IWorldQueryBuilder<T1, T2, T3> With<T>() where T : unmanaged { Builder.With<T>(); return this; }
    public IWorldQueryBuilder<T1, T2, T3> Without<T>() where T : unmanaged { Builder.Without<T>(); return this; }
    public IWorldQuery<T1, T2, T3> Build() => new FlecsQuery<T1, T2, T3>(World, Builder.Build());
}

internal sealed class FlecsQueryBuilder<T1, T2, T3, T4> : FlecsQueryBuilder, IWorldQueryBuilder<T1, T2, T3, T4>
    where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged
{
    public FlecsQueryBuilder(FlecsWorld world) : base(world) { Builder.With<T1>().With<T2>().With<T3>().With<T4>(); }

    public IWorldQueryBuilder<T1, T2, T3, T4> With<T>() where T : unmanaged { Builder.With<T>(); return this; }
    public IWorldQueryBuilder<T1, T2, T3, T4> Without<T>() where T : unmanaged { Builder.Without<T>(); return this; }
    public IWorldQuery<T1, T2, T3, T4> Build() => new FlecsQuery<T1, T2, T3, T4>(World, Builder.Build());
}

internal sealed unsafe class FlecsQuery<T1>(FlecsWorld world, Query query) : FlecsQuery(world, query), IWorldQuery<T1>
    where T1 : unmanaged
{
    public void Each(QueryEachAction<T1> action)
    {
        flecs.ecs_iter_t it = Iter();
        while (Query.GetNext(&it))
        {
            ref T1 c1 = ref FlecsIter.First<T1>(&it, 0);
            for (int i = 0, n = it.count; i < n; i++)
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

internal sealed unsafe class FlecsQuery<T1, T2>(FlecsWorld world, Query query) : FlecsQuery(world, query), IWorldQuery<T1, T2>
    where T1 : unmanaged where T2 : unmanaged
{
    public void Each(QueryEachAction<T1, T2> action)
    {
        flecs.ecs_iter_t it = Iter();
        while (Query.GetNext(&it))
        {
            ref T1 c1 = ref FlecsIter.First<T1>(&it, 0);
            ref T2 c2 = ref FlecsIter.First<T2>(&it, 1);
            for (int i = 0, n = it.count; i < n; i++)
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

internal sealed unsafe class FlecsQuery<T1, T2, T3>(FlecsWorld world, Query query) : FlecsQuery(world, query), IWorldQuery<T1, T2, T3>
    where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged
{
    public void Each(QueryEachAction<T1, T2, T3> action)
    {
        flecs.ecs_iter_t it = Iter();
        while (Query.GetNext(&it))
        {
            ref T1 c1 = ref FlecsIter.First<T1>(&it, 0);
            ref T2 c2 = ref FlecsIter.First<T2>(&it, 1);
            ref T3 c3 = ref FlecsIter.First<T3>(&it, 2);
            for (int i = 0, n = it.count; i < n; i++)
                action(new Handle(it.entities[i]), ref Unsafe.Add(ref c1, i), ref Unsafe.Add(ref c2, i), ref Unsafe.Add(ref c3, i));
        }
    }

    public void Run(QueryChunkAction<T1, T2, T3> action)
    {
        flecs.ecs_iter_t it = Iter();
        while (Query.GetNext(&it))
            action(FlecsIter.Entities(&it), FlecsIter.Column<T1>(&it, 0), FlecsIter.Column<T2>(&it, 1), FlecsIter.Column<T3>(&it, 2));
    }
}

internal sealed unsafe class FlecsQuery<T1, T2, T3, T4>(FlecsWorld world, Query query) : FlecsQuery(world, query), IWorldQuery<T1, T2, T3, T4>
    where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged
{
    public void Each(QueryEachAction<T1, T2, T3, T4> action)
    {
        flecs.ecs_iter_t it = Iter();
        while (Query.GetNext(&it))
        {
            ref T1 c1 = ref FlecsIter.First<T1>(&it, 0);
            ref T2 c2 = ref FlecsIter.First<T2>(&it, 1);
            ref T3 c3 = ref FlecsIter.First<T3>(&it, 2);
            ref T4 c4 = ref FlecsIter.First<T4>(&it, 3);
            for (int i = 0, n = it.count; i < n; i++)
                action(new Handle(it.entities[i]), ref Unsafe.Add(ref c1, i), ref Unsafe.Add(ref c2, i), ref Unsafe.Add(ref c3, i), ref Unsafe.Add(ref c4, i));
        }
    }

    public void Run(QueryChunkAction<T1, T2, T3, T4> action)
    {
        flecs.ecs_iter_t it = Iter();
        while (Query.GetNext(&it))
            action(FlecsIter.Entities(&it), FlecsIter.Column<T1>(&it, 0), FlecsIter.Column<T2>(&it, 1), FlecsIter.Column<T3>(&it, 2), FlecsIter.Column<T4>(&it, 3));
    }
}
