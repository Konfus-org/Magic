using Magic.Contexts.Subscriptions;

namespace Magic.Tests.Contexts.Subscriptions;

public sealed class SubscriptionTests
{
    [Fact]
    public void Disposing_runs_the_end()
    {
        bool ended = false;
        Subscription subscription = new(() => ended = true);

        subscription.Dispose();

        Assert.True(ended);
    }

    [Fact]
    public void Disposing_again_does_nothing()
    {
        int ends = 0;
        Subscription subscription = new(() => ends++);
        subscription.Dispose();

        subscription.Dispose();

        Assert.Equal(1, ends);
    }
}
