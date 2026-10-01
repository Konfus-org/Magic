using Magic.Contexts.Events;
using Magic.Services;
using Xunit;

namespace Magic.UnitTests.Services;

public sealed class EventsTests
{
    [Fact]
    public void Next_frame_hands_over_what_was_published_in_order()
    {
        Events events = new();
        events.Publish(new Event(EventType.AssetAdded, Id: 1));
        events.Publish(new Event(EventType.GemsChanged));

        Event[] frame = events.NextFrame();

        Assert.Equal([EventType.AssetAdded, EventType.GemsChanged], frame.Select(published => published.Type));
    }

    [Fact]
    public void Next_frame_empties_the_queue()
    {
        Events events = new();
        events.Publish(new Event(EventType.GemsChanged));
        events.NextFrame();

        Event[] frame = events.NextFrame();

        Assert.Empty(frame);
    }

    [Fact]
    public void Events_published_from_other_threads_all_arrive()
    {
        Events events = new();

        Parallel.For(0, 1000, i => events.Publish(new Event(EventType.AssetModified, Id: (ulong)i)));

        Assert.Equal(1000, events.NextFrame().Length);
    }

    [Fact]
    public void A_watch_hears_only_its_type()
    {
        Events events = new();
        List<ulong> heard = [];
        using IDisposable watch = events.Watch(EventType.AssetRemoved, published => heard.Add(published.Id));
        events.Publish(new Event(EventType.AssetRemoved, Id: 7));
        events.Publish(new Event(EventType.AssetAdded, Id: 8));

        events.NextFrame();

        Assert.Equal([7ul], heard);
    }

    [Fact]
    public void A_watch_hears_nothing_until_the_frame_is_taken()
    {
        Events events = new();
        int heard = 0;
        using IDisposable watch = events.Watch(EventType.GemsChanged, _ => heard++);

        events.Publish(new Event(EventType.GemsChanged));

        Assert.Equal(0, heard);
    }

    [Fact]
    public void A_disposed_watch_hears_nothing_more()
    {
        Events events = new();
        int heard = 0;
        IDisposable watch = events.Watch(EventType.GemsChanged, _ => heard++);
        events.Publish(new Event(EventType.GemsChanged));

        watch.Dispose();
        events.NextFrame();

        Assert.Equal(0, heard);
    }

    [Fact]
    public void A_throwing_watch_does_not_stop_the_others()
    {
        Events events = new();
        int heard = 0;
        using IDisposable thrower = events.Watch(EventType.GemsChanged, _ => throw new InvalidOperationException());
        using IDisposable listener = events.Watch(EventType.GemsChanged, _ => heard++);
        events.Publish(new Event(EventType.GemsChanged));

        events.NextFrame();

        Assert.Equal(1, heard);
    }
}
