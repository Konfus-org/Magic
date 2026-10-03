using Magic.Utils;
using Xunit;

namespace Magic.UnitTests.Utils;

public sealed class SubscriptionTests
{
    [Fact]
    public void Disposing_ends_it()
    {
        int ended = 0;
        Subscription subscription = new(() => ended++);

        subscription.Dispose();

        Assert.Equal(1, ended);
    }

    [Fact]
    public void Disposing_twice_ends_it_once()
    {
        int ended = 0;
        Subscription subscription = new(() => ended++);
        subscription.Dispose();

        subscription.Dispose();

        Assert.Equal(1, ended);
    }
}
