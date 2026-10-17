using Magic.Contexts.Subscriptions;

namespace Magic.Tests.Contexts.Subscriptions;

public sealed class SubscriptionListTests
{
    [Fact]
    public void Disposing_ends_every_subscription_added()
    {
        bool firstEnded = false;
        bool secondEnded = false;
        SubscriptionList list = new();
        list.Add(new Subscription(() => firstEnded = true));
        list.Add(new Subscription(() => secondEnded = true));

        list.Dispose();

        Assert.True(firstEnded && secondEnded);
    }

    [Fact]
    public void Disposing_ends_the_last_added_first()
    {
        List<string> ended = [];
        SubscriptionList list = new();
        list.Add(new Subscription(() => ended.Add("first")));
        list.Add(new Subscription(() => ended.Add("second")));

        list.Dispose();

        string[] expected = ["second", "first"];
        Assert.Equal(expected, ended, StringComparer.Ordinal);
    }

    [Fact]
    public void Disposing_again_ends_nothing_more()
    {
        int ends = 0;
        SubscriptionList list = new();
        list.Add(new Subscription(() => ends++));
        list.Dispose();

        list.Dispose();

        Assert.Equal(1, ends);
    }
}
