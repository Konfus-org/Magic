using Magic.Services;
using Xunit;

namespace Magic.Tests;

public sealed class EventBusTests
{
    private readonly record struct Ping(int Value);
    private readonly record struct Pong(int Value);

    [Fact]
    public void Handlers_get_their_own_type_only_until_disposed()
    {
        EventBus bus = new();
        List<int> pings = [];
        List<int> pongs = [];
        IDisposable ping = bus.Subscribe<Ping>(e => pings.Add(e.Value));
        using IDisposable pong = bus.Subscribe<Pong>(e => pongs.Add(e.Value));

        bus.Publish(new Ping(1));
        bus.Publish(new Pong(2));
        ping.Dispose();
        ping.Dispose(); // twice is fine
        bus.Publish(new Ping(3));

        Assert.Equal([1], pings);
        Assert.Equal([2], pongs);
    }

    [Fact]
    public void A_throwing_handler_does_not_stop_the_others()
    {
        EventBus bus = new();
        int reached = 0;
        using IDisposable bad = bus.Subscribe<Ping>(_ => throw new InvalidOperationException("boom"));
        using IDisposable good = bus.Subscribe<Ping>(_ => reached++);

        bus.Publish(new Ping(1));

        Assert.Equal(1, reached);
    }

    [Fact]
    public void Publishing_with_nobody_listening_is_fine()
    {
        new EventBus().Publish(new Ping(1));
    }
}
