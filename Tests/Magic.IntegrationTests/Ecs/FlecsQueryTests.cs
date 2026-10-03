using FlecsGem;
using Magic.Contexts;
using Magic.Interfaces;
using Xunit;

namespace Magic.IntegrationTests.Ecs;

/// <summary>
/// The adapter's own iteration over flecs' tables, and the parent column first cascade shares.
/// </summary>
public sealed class FlecsQueryTests
{
    [Fact]
    public void Each_visits_every_matching_entity()
    {
        using FlecsEcs ecs = new();
        Handle first = Spawn(ecs, 1);
        Handle second = Spawn(ecs, 2);
        using IEcsQuery<Position> query = ecs.Query<Position>().Build();
        List<Handle> seen = [];

        query.Each((Handle entity, ref Position _) => seen.Add(entity));

        Assert.Equal([first, second], seen.OrderBy(handle => handle.Id));
    }

    [Fact]
    public void Each_writes_through_to_the_component()
    {
        using FlecsEcs ecs = new();
        Handle first = Spawn(ecs, 1);
        using IEcsQuery<Position> query = ecs.Query<Position>().Build();

        query.Each((Handle _, ref Position position) => position.X = 11);

        Assert.Equal(11, ecs.Get<Position>(first).X);
    }

    [Fact]
    public void Run_writes_through_to_the_component()
    {
        using FlecsEcs ecs = new();
        Handle first = Spawn(ecs, 1);
        using IEcsQuery<Position> query = ecs.Query<Position>().Build();

        query.Run((ReadOnlySpan<Handle> ids, Span<Position> positions) => positions.Fill(new Position { X = 10 }));

        Assert.Equal(10, ecs.Get<Position>(first).X);
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
            foreach (Handle entity in entities)
                parents[entity] = parent.IsEmpty ? -1 : parent[0].Value;
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

    [Fact]
    public void WithoutAbove_skips_an_entity_that_has_the_component()
    {
        using FlecsEcs ecs = new();
        Handle entity = Spawn(ecs, 1);
        ecs.Add<Marked>(entity);
        using IEcsQuery<Position> query = ecs.Query<Position>().WithoutAbove<Marked>().Build();

        Assert.Equal(0, query.Count());
    }

    [Fact]
    public void WithoutAbove_skips_an_entity_under_one_that_has_the_component()
    {
        using FlecsEcs ecs = new();
        Handle root = ecs.Create();
        ecs.Add<Marked>(root);
        Handle middle = ecs.Create("Middle", root);
        ecs.SetParent(Spawn(ecs, 1), middle);
        using IEcsQuery<Position> query = ecs.Query<Position>().WithoutAbove<Marked>().Build();

        Assert.Equal(0, query.Count());
    }

    [Fact]
    public void WithoutAbove_matches_again_once_the_component_above_is_removed()
    {
        using FlecsEcs ecs = new();
        Handle root = ecs.Create();
        ecs.Add<Marked>(root);
        ecs.SetParent(Spawn(ecs, 1), root);
        using IEcsQuery<Position> query = ecs.Query<Position>().WithoutAbove<Marked>().Build();
        query.Count();

        ecs.Remove<Marked>(root);

        Assert.Equal(1, query.Count());
    }

    private static Handle Spawn(FlecsEcs ecs, float x)
    {
        Handle entity = ecs.Create();
        ecs.Set(entity, new Position { X = x });

        return entity;
    }

    private static Handle Spawn(FlecsEcs ecs, string? name, Handle parent, int composed)
    {
        Handle entity = ecs.Create(name, parent);
        ecs.Set(entity, new Local());
        ecs.Set(entity, new Composed { Value = composed });

        return entity;
    }
}
