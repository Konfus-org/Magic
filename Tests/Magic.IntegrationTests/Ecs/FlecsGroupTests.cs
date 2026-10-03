using FlecsGem;
using Magic.Contexts;
using Magic.Contexts.Rendering;
using Magic.Interfaces;
using Xunit;

namespace Magic.IntegrationTests.Ecs;

/// <summary>
/// Groups: the entities made inside one are made together when it ends, with everything they were given.
/// </summary>
public sealed class FlecsGroupTests
{
    [Fact]
    public void A_component_set_in_a_group_is_there_once_the_group_ends()
    {
        using FlecsEcs ecs = new();
        Handle entity;

        using (ecs.Group())
        {
            entity = ecs.Create();
            ecs.Set(entity, new Position { X = 4 });
        }

        Assert.Equal(4, ecs.Get<Position>(entity).X);
    }

    [Fact]
    public void What_an_entity_made_in_a_group_was_given_cannot_be_read_back_before_the_group_ends()
    {
        using FlecsEcs ecs = new();
        bool had;

        using (ecs.Group())
        {
            Handle entity = ecs.Create();
            ecs.Set(entity, new Position { X = 4 });
            had = ecs.Has<Position>(entity);
        }

        Assert.False(had);
    }

    [Fact]
    public void An_entity_made_in_a_group_keeps_every_component_it_was_given()
    {
        using FlecsEcs ecs = new();
        Handle entity;

        using (ecs.Group())
        {
            entity = ecs.Create();
            ecs.Set(entity, new Position { X = 1 });
            ecs.Set(entity, new Velocity { X = 2 });
        }

        Assert.True(ecs.Has<Position>(entity) && ecs.Has<Velocity>(entity));
    }

    [Fact]
    public void An_entity_made_in_a_group_is_a_child_of_a_parent_made_in_it()
    {
        using FlecsEcs ecs = new();
        Handle parent, child;

        using (ecs.Group())
        {
            parent = ecs.Create("Parent");
            child = ecs.Create("Child", parent);
        }

        Assert.Equal(parent, ecs.GetParent(child));
    }

    [Fact]
    public void An_entity_named_in_a_group_is_found_by_its_path_once_the_group_ends()
    {
        using FlecsEcs ecs = new();
        Handle child;

        using (ecs.Group())
            child = ecs.Create("Child", ecs.Create("Parent"));

        Assert.Equal(child, ecs.Lookup("Parent.Child"));
    }

    [Fact]
    public void A_component_added_to_an_entity_made_in_a_group_starts_at_its_default()
    {
        using FlecsEcs ecs = new();
        Handle entity;

        using (ecs.Group())
        {
            entity = ecs.Create();
            ecs.Add<Position>(entity);
        }

        Assert.Equal(0, ecs.Get<Position>(entity).X);
    }

    [Fact]
    public void A_nested_group_is_applied_when_the_outer_one_ends()
    {
        using FlecsEcs ecs = new();
        bool had;

        using (ecs.Group())
        {
            Handle entity;
            using (ecs.Group())
            {
                entity = ecs.Create();
                ecs.Set(entity, new Position { X = 4 });
            }

            had = ecs.Has<Position>(entity);
        }

        Assert.False(had);
    }

    [Fact]
    public void A_group_inside_a_system_is_applied_when_it_ends()
    {
        using FlecsEcs ecs = new();
        bool had = false;
        using IDisposable system = ecs.Schedule("Grouping").Run(_ =>
        {
            Handle entity;
            using (ecs.Group())
            {
                entity = ecs.Create();
                ecs.Set(entity, new Position { X = 4 });
            }

            had = ecs.Has<Position>(entity);
        });

        ecs.Update(new Frame(1, 0, 0.016f, default, new RenderCommands()));

        Assert.True(had);
    }

    [Fact]
    public void An_entity_made_before_the_group_changes_at_once_inside_it()
    {
        using FlecsEcs ecs = new();
        Handle entity = ecs.Create();
        bool had;

        using (ecs.Group())
        {
            ecs.Set(entity, new Position { X = 4 });
            had = ecs.Has<Position>(entity);
        }

        Assert.True(had);
    }

    [Fact]
    public void A_component_given_twice_in_a_group_keeps_the_later_value()
    {
        using FlecsEcs ecs = new();
        Handle entity;

        using (ecs.Group())
        {
            entity = ecs.Create();
            ecs.Set(entity, new Position { X = 1 });
            ecs.Set(entity, new Position { X = 2 });
        }

        Assert.Equal(2, ecs.Get<Position>(entity).X);
    }

    [Fact]
    public void Entities_alike_made_in_a_group_each_keep_their_own_values()
    {
        using FlecsEcs ecs = new();
        Handle[] entities = new Handle[100];

        using (ecs.Group())
        {
            Handle parent = ecs.Create();
            for (int i = 0; i < entities.Length; i++)
            {
                entities[i] = ecs.Create(parent: parent);
                ecs.Set(entities[i], new Position { X = i });
                ecs.Set(entities[i], new Velocity { X = -i });
            }
        }

        Assert.Equal(Enumerable.Range(0, 100).Select(i => ((float)i, (float)-i)), entities.Select(entity => (ecs.Get<Position>(entity).X, ecs.Get<Velocity>(entity).X)));
    }

    [Fact]
    public void Entities_given_different_components_in_a_group_each_get_their_own()
    {
        using FlecsEcs ecs = new();
        Handle moving, still;

        using (ecs.Group())
        {
            still = ecs.Create();
            ecs.Set(still, new Position { X = 1 });
            moving = ecs.Create();
            ecs.Set(moving, new Position { X = 2 });
            ecs.Set(moving, new Velocity { X = 3 });
        }

        Assert.Equal((false, true), (ecs.Has<Velocity>(still), ecs.Has<Velocity>(moving)));
    }

    [Fact]
    public void Destroying_an_entity_made_in_a_group_inside_it_leaves_it_gone()
    {
        using FlecsEcs ecs = new();
        Handle entity;

        using (ecs.Group())
        {
            entity = ecs.Create();
            ecs.Set(entity, new Position { X = 1 });
            ecs.Destroy(entity);
        }

        Assert.False(ecs.IsAlive(entity));
    }

    [Fact]
    public void A_query_finds_the_entities_made_in_a_group_once_it_ends()
    {
        using FlecsEcs ecs = new();
        using IEcsQuery<Position> positions = ecs.Query<Position>().Build();

        using (ecs.Group())
        {
            for (int i = 0; i < 10; i++)
                ecs.Set(ecs.Create(), new Position { X = i });
        }

        Assert.Equal(10, positions.Count());
    }

    [Fact]
    public void An_observer_hears_of_a_grouped_set_when_the_group_ends()
    {
        using FlecsEcs ecs = new();
        int heardInside = -1, heard = 0;
        using IDisposable observer = ecs.Observe<Position>(ComponentEvent.Set, _ => heard++);

        using (ecs.Group())
        {
            ecs.Set(ecs.Create(), new Position { X = 4 });
            heardInside = heard;
        }

        Assert.Equal((0, 1), (heardInside, heard));
    }
}
