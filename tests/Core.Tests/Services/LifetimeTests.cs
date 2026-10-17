using Magic.Services;

namespace Magic.Tests.Services;

public sealed class LifetimeTests
{
    [Fact]
    public void Stop_ends_the_run()
    {
        using Lifetime lifetime = new();

        lifetime.Stop("the test is done");

        Assert.True(lifetime.IsStopping);
    }

    [Fact]
    public void A_token_taken_before_the_stop_is_cancelled_by_it()
    {
        using Lifetime lifetime = new();
        CancellationToken stopping = lifetime.Stopping;

        lifetime.Stop("the test is done");

        Assert.True(stopping.IsCancellationRequested);
    }

    [Fact]
    public void Stop_is_not_a_restart()
    {
        using Lifetime lifetime = new();

        lifetime.Stop("the test is done");

        Assert.False(lifetime.IsRestarting);
    }

    [Fact]
    public void Restart_ends_the_run()
    {
        using Lifetime lifetime = new();

        lifetime.Restart("a setting changed");

        Assert.True(lifetime.IsStopping);
    }

    [Fact]
    public void Restart_ends_the_run_to_start_it_again()
    {
        using Lifetime lifetime = new();

        lifetime.Restart("a setting changed");

        Assert.True(lifetime.IsRestarting);
    }

    [Fact]
    public void A_restart_once_the_run_is_stopping_changes_nothing()
    {
        using Lifetime lifetime = new();
        lifetime.Stop("the test is done");

        lifetime.Restart("a setting changed");

        Assert.False(lifetime.IsRestarting);
    }
}
