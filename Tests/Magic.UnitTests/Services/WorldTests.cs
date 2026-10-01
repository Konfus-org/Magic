using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Events;
using Magic.Services;
using Xunit;

namespace Magic.UnitTests.Services;

public sealed class WorldTests
{
    private static readonly Handle<Domain> Cave = new(1);
    private static readonly Handle<Domain> Town = new(2);

    [Fact]
    public void Opening_a_domain_publishes_that_it_opened()
    {
        Events events = new();
        World world = new(events);

        world.Open(Cave);

        Assert.Equal([new Event(EventType.DomainOpened, Id: 1)], events.NextFrame());
    }

    [Fact]
    public void Opening_a_domain_closes_the_open_ones()
    {
        World world = new(new Events());
        world.Open(Cave);

        world.Open(Town);

        Assert.Equal([Town], world.Active);
    }

    [Fact]
    public void Replacing_a_domain_publishes_its_close_before_the_open()
    {
        Events events = new();
        World world = new(events);
        world.Open(Cave);
        events.NextFrame();

        world.Open(Town);

        Assert.Equal([new Event(EventType.DomainClosed, Id: 1), new Event(EventType.DomainOpened, Id: 2)], events.NextFrame());
    }

    [Fact]
    public void An_additive_open_keeps_the_open_ones()
    {
        World world = new(new Events());
        world.Open(Cave);

        world.Open(Town, OpenMode.Additive);

        Assert.Equal([Cave, Town], world.Active);
    }

    [Fact]
    public void An_additive_open_of_an_open_domain_publishes_nothing()
    {
        Events events = new();
        World world = new(events);
        world.Open(Cave);
        events.NextFrame();

        world.Open(Cave, OpenMode.Additive);

        Assert.Empty(events.NextFrame());
    }

    [Fact]
    public void Closing_a_domain_leaves_the_others_open()
    {
        World world = new(new Events());
        world.Open(Cave);
        world.Open(Town, OpenMode.Additive);

        world.Close(Cave);

        Assert.Equal([Town], world.Active);
    }

    [Fact]
    public void Closing_a_domain_publishes_that_it_closed()
    {
        Events events = new();
        World world = new(events);
        world.Open(Cave);
        events.NextFrame();

        world.Close(Cave);

        Assert.Equal([new Event(EventType.DomainClosed, Id: 1)], events.NextFrame());
    }

    [Fact]
    public void Closing_a_domain_that_is_not_open_publishes_nothing()
    {
        Events events = new();
        World world = new(events);

        world.Close(Cave);

        Assert.Empty(events.NextFrame());
    }

    [Fact]
    public void Opening_the_void_closes_every_domain()
    {
        World world = new(new Events());
        world.Open(Cave);
        world.Open(Town, OpenMode.Additive);

        world.Open(Handle<Domain>.None);

        Assert.Empty(world.Active);
    }

    [Fact]
    public void Ending_the_world_publishes_quit()
    {
        Events events = new();
        World world = new(events);

        world.End();

        Assert.Equal([new Event(EventType.Quit)], events.NextFrame());
    }
}
