using FlecsGem;
using Magic.Contexts;
using Magic.Interfaces;
using Xunit;

namespace Magic.IntegrationTests.Ecs;

/// <summary>What the adapter adds on top of flecs; flecs itself is trusted.</summary>
public sealed class FlecsEcsTests
{
    [Fact]
    public void Add_gives_a_default_value_even_on_recycled_memory()
    {
        // flecs hands back the column memory as it was; a removed and re-added component must still read as default.
        using FlecsEcs ecs = new();
        Handle e = ecs.Create();
        ecs.Set(e, new Position { X = 5 });
        ecs.Remove<Position>(e);

        ecs.Add<Position>(e);

        Assert.Equal(0, ecs.Get<Position>(e).X);
    }

    [Fact]
    public void Add_of_a_present_component_keeps_its_value()
    {
        using FlecsEcs ecs = new();
        Handle e = ecs.Create();
        ecs.Set(e, new Position { X = 3 });

        ecs.Add<Position>(e);

        Assert.Equal(3, ecs.Get<Position>(e).X);
    }

    [Fact]
    public void Get_of_a_missing_component_throws()
    {
        using FlecsEcs ecs = new();
        Handle e = ecs.Create();

        Action get = () => ecs.Get<Position>(e);

        Assert.Throws<InvalidOperationException>(get);
    }

    [Fact]
    public void An_unnamed_entity_has_no_name()
    {
        using FlecsEcs ecs = new();
        Handle e = ecs.Create();

        string? name = ecs.GetName(e);

        Assert.Null(name);
    }

    [Fact]
    public void Setting_no_parent_detaches_a_child()
    {
        using FlecsEcs ecs = new();
        Handle player = ecs.Create("Player", ecs.Create("Level"));

        ecs.SetParent(player, Handle.None);

        Assert.Equal(Handle.None, ecs.GetParent(player));
    }

    [Fact]
    public void The_none_handle_is_never_alive()
    {
        using FlecsEcs ecs = new();

        bool alive = ecs.IsAlive(Handle.None);

        Assert.False(alive);
    }

    [Fact]
    public void An_added_observer_hears_a_component_added()
    {
        using FlecsEcs ecs = new();
        List<Handle> heard = [];
        using IDisposable observer = ecs.Observe<Position>(ComponentEvent.Added, heard.Add);
        Handle e = ecs.Create();

        ecs.Add<Position>(e);

        Assert.Equal([e], heard);
    }

    [Fact]
    public void A_set_observer_hears_a_component_set()
    {
        using FlecsEcs ecs = new();
        List<Handle> heard = [];
        using IDisposable observer = ecs.Observe<Position>(ComponentEvent.Set, heard.Add);
        Handle e = ecs.Create();

        ecs.Set(e, new Position { X = 1 });

        Assert.Equal([e], heard);
    }

    [Fact]
    public void A_removed_observer_hears_a_component_removed()
    {
        using FlecsEcs ecs = new();
        List<Handle> heard = [];
        using IDisposable observer = ecs.Observe<Position>(ComponentEvent.Removed, heard.Add);
        Handle e = ecs.Create();
        ecs.Set(e, new Position());

        ecs.Remove<Position>(e);

        Assert.Equal([e], heard);
    }

    [Fact]
    public void A_disposed_observer_hears_nothing_more()
    {
        using FlecsEcs ecs = new();
        List<Handle> heard = [];
        IDisposable observer = ecs.Observe<Position>(ComponentEvent.Added, heard.Add);

        observer.Dispose();
        ecs.Add<Position>(ecs.Create());

        Assert.Empty(heard);
    }
}
