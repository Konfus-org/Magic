using FlecsGem;
using Magic.Contexts;
using Magic.Interfaces;
using Magic.Services;
using Xunit;

namespace Magic.Tests.Ecs;

public struct Position { public float X, Y; }
public struct Velocity { public float X, Y; }
public struct Frozen { }

public sealed class FlecsWorldTests : IDisposable
{
    private readonly SystemScheduler _scheduler = new();
    private readonly FlecsWorld _world;

    public FlecsWorldTests()
    {
        _world = new FlecsWorld(_scheduler);
    }

    public void Dispose()
    {
        _world.Dispose();
    }

    private Handle Spawn(float x, float vx, bool frozen = false)
    {
        Handle e = _world.Create();
        _world.Set(e, new Position { X = x });
        _world.Set(e, new Velocity { X = vx });
        if (frozen)
            _world.Add<Frozen>(e);
        return e;
    }

    [Fact]
    public void Query_Each_visits_every_matching_entity_by_reference()
    {
        Handle a = Spawn(1, 10);
        Handle b = Spawn(2, 20);
        using IWorldQuery<Position, Velocity> query = _world.Query<Position, Velocity>().Build();

        HashSet<Handle> seen = [];
        query.Each((Handle e, ref Position p, ref Velocity v) => { seen.Add(e); p.X += v.X; });

        Assert.Equal([a, b], seen.OrderBy(h => h.Id));
        Assert.Equal(11, _world.Get<Position>(a).X);
        Assert.Equal(22, _world.Get<Position>(b).X);
        Assert.Equal(2, query.Count());
    }

    [Fact]
    public void Query_Run_hands_out_columns_that_line_up_with_entities()
    {
        Handle a = Spawn(1, 10);
        Handle b = Spawn(2, 20);
        using IWorldQuery<Position, Velocity> query = _world.Query<Position, Velocity>().Build();

        int chunks = 0, entities = 0;
        query.Run((ReadOnlySpan<Handle> ids, Span<Position> ps, Span<Velocity> vs) =>
        {
            chunks++;
            Assert.Equal(ids.Length, ps.Length);
            Assert.Equal(ids.Length, vs.Length);
            for (int i = 0; i < ids.Length; i++)
            {
                entities++;
                Assert.Equal(_world.Get<Position>(ids[i]).X, ps[i].X);
                ps[i].X = vs[i].X;
            }
        });

        Assert.Equal(1, chunks); // same archetype, one table
        Assert.Equal(2, entities);
        Assert.Equal(10, _world.Get<Position>(a).X);
        Assert.Equal(20, _world.Get<Position>(b).X);
    }

    [Fact]
    public void Query_With_and_Without_filter_by_tag()
    {
        Handle moving = Spawn(0, 1);
        Handle frozen = Spawn(0, 1, frozen: true);

        using IWorldQuery<Position> notFrozen = _world.Query<Position>().Without<Frozen>().Build();
        using IWorldQuery<Position> onlyFrozen = _world.Query<Position>().With<Frozen>().Build();

        List<Handle> a = [], b = [];
        notFrozen.Each((Handle e, ref Position _) => a.Add(e));
        onlyFrozen.Each((Handle e, ref Position _) => b.Add(e));

        Assert.Equal([moving], a);
        Assert.Equal([frozen], b);
    }

    [Fact]
    public void Query_skips_disabled_entities()
    {
        Handle on = Spawn(0, 0);
        Handle off = Spawn(0, 0);
        _world.Enable(off, false);
        using IWorldQuery<Position> query = _world.Query<Position>().Build();

        List<Handle> seen = [];
        query.Each((Handle e, ref Position _) => seen.Add(e));

        Assert.Equal([on], seen);
        Assert.False(_world.IsEnabled(off));
    }

    [Fact]
    public void Scheduled_system_runs_on_its_phase_with_delta_in_seconds()
    {
        Handle e = Spawn(0, 4);
        using IDisposable system = _world.Schedule("Move").On(UpdateType.FixedUpdate).Query<Position, Velocity>()
            .Each((float dt, Handle _, ref Position p, ref Velocity v) => p.X += v.X * dt);

        _scheduler.Update(500);
        Assert.Equal(0, _world.Get<Position>(e).X);

        _scheduler.FixedUpdate(500);
        Assert.Equal(2, _world.Get<Position>(e).X);
    }

    [Fact]
    public void Scheduled_chunk_system_and_plain_callback_run_in_order()
    {
        Spawn(0, 1);
        List<string> order = [];
        using IDisposable first = _world.Schedule("First").Run(dt => order.Add($"first {dt}"));
        using IDisposable second = _world.Schedule("Second").Query<Position>()
            .Run((float dt, ReadOnlySpan<Handle> ids, Span<Position> ps) => order.Add($"second {ids.Length}"));

        _scheduler.Update(1000);

        Assert.Equal(["first 1", "second 1"], order);
    }

    [Fact]
    public void Disposing_a_system_stops_it()
    {
        Handle e = Spawn(0, 1);
        IDisposable system = _world.Schedule("Move").Query<Position, Velocity>()
            .Each((float dt, Handle _, ref Position p, ref Velocity v) => p.X += v.X);

        _scheduler.Update(1000);
        system.Dispose();
        _scheduler.Update(1000);

        Assert.Equal(1, _world.Get<Position>(e).X);
    }

    [Fact]
    public void Parallel_system_visits_every_entity_once()
    {
        Handle[] ids = new Handle[10_000];
        _world.Create(ids);
        foreach (Handle id in ids)
            _world.Set(id, new Position());

        int visits = 0;
        using IDisposable system = _world.Schedule("Count").Query<Position>().Parallel()
            .Each((float _, Handle _, ref Position p) => { p.X += 1; Interlocked.Increment(ref visits); });

        _scheduler.Update(16);

        Assert.Equal(ids.Length, visits);
        Assert.All(ids, id => Assert.Equal(1, _world.Get<Position>(id).X));
    }

    [Fact]
    public void Hierarchy_and_names_round_trip()
    {
        Handle level = _world.Create("Level");
        Handle player = _world.Create("Player", level);

        Assert.Equal(level, _world.GetParent(player));
        Assert.Equal([player], _world.GetChildren(level));
        Assert.Equal(player, _world.Lookup("Level.Player"));
        Assert.True(_world.IsAlive(player));
        Assert.False(_world.IsAlive(Handle.None));

        _world.SetParent(player, Handle.None);
        Assert.Equal(Handle.None, _world.GetParent(player));

        _world.Destroy(level);
        Assert.False(_world.IsAlive(level));
        Assert.True(_world.IsAlive(player));
    }
}
