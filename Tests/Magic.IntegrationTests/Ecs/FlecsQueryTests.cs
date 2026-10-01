using FlecsGem;
using Magic.Contexts;
using Magic.Interfaces;
using Xunit;

namespace Magic.IntegrationTests.Ecs;

/// <summary>The adapter's own iteration over flecs' tables, and the parent column a cascade shares.</summary>
public sealed class FlecsQueryTests
{
    [Fact]
    public void Each_visits_every_matching_entity()
    {
        using FlecsEcs ecs = new();
        Handle a = Spawn(ecs, 1);
        Handle b = Spawn(ecs, 2);
        using IEcsQuery<Position> query = ecs.Query<Position>().Build();
        List<Handle> seen = [];

        query.Each((Handle e, ref Position _) => seen.Add(e));

        Assert.Equal([a, b], seen.OrderBy(h => h.Id));
    }

    [Fact]
    public void Each_writes_through_to_the_component()
    {
        using FlecsEcs ecs = new();
        Handle a = Spawn(ecs, 1);
        using IEcsQuery<Position> query = ecs.Query<Position>().Build();

        query.Each((Handle _, ref Position p) => p.X = 11);

        Assert.Equal(11, ecs.Get<Position>(a).X);
    }

    [Fact]
    public void Run_writes_through_to_the_component()
    {
        using FlecsEcs ecs = new();
        Handle a = Spawn(ecs, 1);
        using IEcsQuery<Position> query = ecs.Query<Position>().Build();

        query.Run((ReadOnlySpan<Handle> ids, Span<Position> positions) => positions.Fill(new Position { X = 10 }));

        Assert.Equal(10, ecs.Get<Position>(a).X);
    }

    [Fact]
    public void Cascade_run_hands_a_child_its_parents_value()
    {
        using FlecsEcs ecs = new();
        Handle root = Spawn(ecs, null, default, composed: 7);
        Handle child = Spawn(ecs, "Child", root, composed: 0);
        using IEcsQuery<Local, Composed, Composed> query = ecs.Query<Local, Composed, Composed>().Cascade().Build();
        Dictionary<Handle, int> parents = [];

        query.Run((ReadOnlySpan<Handle> entities, Span<Local> _, Span<Composed> _, Span<Composed> parent) =>
        {
            foreach (Handle e in entities)
                parents[e] = parent.IsEmpty ? -1 : parent[0].Value;
        });

        Assert.Equal(7, parents[child]);
    }

    [Fact]
    public void Cascade_run_hands_a_root_no_parent_column()
    {
        using FlecsEcs ecs = new();
        Handle root = Spawn(ecs, null, default, composed: 7);
        using IEcsQuery<Local, Composed, Composed> query = ecs.Query<Local, Composed, Composed>().Cascade().Build();
        int parentLength = -1;

        query.Run((ReadOnlySpan<Handle> _, Span<Local> _, Span<Composed> _, Span<Composed> parent) => parentLength = parent.Length);

        Assert.Equal(0, parentLength);
    }

    [Fact]
    public void Cascade_each_reads_an_absent_parent_as_default()
    {
        using FlecsEcs ecs = new();
        Handle root = Spawn(ecs, null, default, composed: 7);
        using IEcsQuery<Local, Composed, Composed> query = ecs.Query<Local, Composed, Composed>().Cascade().Build();
        int parentValue = -1;

        query.Each((Handle _, ref Local _, ref Composed _, ref Composed parent) => parentValue = parent.Value);

        Assert.Equal(0, parentValue);
    }

    private static Handle Spawn(FlecsEcs ecs, float x)
    {
        Handle e = ecs.Create();
        ecs.Set(e, new Position { X = x });

        return e;
    }

    private static Handle Spawn(FlecsEcs ecs, string? name, Handle parent, int composed)
    {
        Handle e = ecs.Create(name, parent);
        ecs.Set(e, new Local());
        ecs.Set(e, new Composed { Value = composed });

        return e;
    }
}
