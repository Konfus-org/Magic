using DeferredRendererGem;
using Xunit;

namespace Magic.UnitTests.Render;

public sealed class PendingTests
{
    [Fact]
    public void A_finished_job_is_delivered()
    {
        Pending<int, string> pending = new();
        List<(int Key, string Value)> done = [];
        pending.Start(1, _ => Task.FromResult("a"));

        pending.Poll(done, Record);

        Assert.Equal([(1, "a")], done);
    }

    [Fact]
    public void A_delivered_job_is_not_delivered_again()
    {
        Pending<int, string> pending = new();
        List<(int Key, string Value)> done = [];
        pending.Start(1, _ => Task.FromResult("a"));
        pending.Poll(done, Record);

        pending.Poll(done, Record);

        Assert.Single(done);
    }

    [Fact]
    public void A_running_job_is_not_delivered()
    {
        Pending<int, string> pending = new();
        List<(int Key, string Value)> done = [];
        pending.Start(1, _ => new TaskCompletionSource<string>().Task);

        pending.Poll(done, Record);

        Assert.Empty(done);
    }

    [Fact]
    public void A_superseded_job_is_never_delivered()
    {
        Pending<int, string> pending = new();
        List<(int Key, string Value)> done = [];
        pending.Start(1, _ => Task.FromResult("stale"));
        pending.Start(1, _ => Task.FromResult("newest"));

        pending.Poll(done, Record);

        Assert.Equal([(1, "newest")], done);
    }

    [Fact]
    public void A_superseded_job_is_cancelled()
    {
        Pending<int, string> pending = new();
        CancellationToken stale = default;
        pending.Start(1, cancel =>
        {
            stale = cancel;
            return new TaskCompletionSource<string>().Task;
        });

        pending.Start(1, _ => Task.FromResult("newest"));

        Assert.True(stale.IsCancellationRequested);
    }

    [Fact]
    public void A_faulted_job_is_delivered_with_what_it_threw()
    {
        Pending<int, string> pending = new();
        List<(int Key, string Value)> done = [];
        pending.Start(1, _ => Task.FromException<string>(new InvalidOperationException("boom")));

        pending.Poll(done, Record);

        Assert.Equal([(1, "boom")], done);
    }

    private static void Record(List<(int Key, string Value)> done, int key, Task<string> job)
    {
        done.Add((key, job.IsCompletedSuccessfully ? job.Result : job.Exception?.GetBaseException().Message ?? ""));
    }
}
