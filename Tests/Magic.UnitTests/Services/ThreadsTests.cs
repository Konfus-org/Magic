using Magic.Contexts.Threading;
using Magic.Services;
using Xunit;

namespace Magic.UnitTests.Services;

public sealed class ThreadsTests
{
    [Fact]
    public void Work_invoked_for_a_worker_runs_on_the_calling_thread()
    {
        using Threads threads = new();
        int caller = Environment.CurrentManagedThreadId;

        int ran = threads.Invoke(ThreadId.Worker, () => Environment.CurrentManagedThreadId);

        Assert.Equal(caller, ran);
    }

    [Fact]
    public async Task Work_awaited_for_a_worker_runs_on_a_worker()
    {
        using Threads threads = new();

        bool onWorker = await threads.InvokeAsync(ThreadId.Worker, _ => Thread.CurrentThread.IsThreadPoolThread);

        Assert.True(onWorker);
    }

    [Fact]
    public void Work_invoked_on_a_dedicated_thread_runs_on_it()
    {
        using Threads threads = new();
        ThreadId audio = threads.Create("Audio");

        string? name = threads.Invoke(audio, () => Thread.CurrentThread.Name);

        Assert.Equal("Audio", name);
    }

    [Fact]
    public async Task Work_awaited_on_a_dedicated_thread_runs_on_it()
    {
        using Threads threads = new();
        ThreadId audio = threads.Create("Audio");

        string? name = await threads.InvokeAsync(audio, _ => Thread.CurrentThread.Name);

        Assert.Equal("Audio", name);
    }

    [Fact]
    public async Task Work_awaited_for_a_worker_and_a_cancelled_token_does_not_run()
    {
        using Threads threads = new();
        bool ran = false;

        Task work = threads.InvokeAsync(ThreadId.Worker, _ => ran = true, new CancellationToken(canceled: true));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        Assert.False(ran);
    }

    [Fact]
    public async Task Work_awaited_on_a_dedicated_thread_with_a_cancelled_token_does_not_run()
    {
        using Threads threads = new();
        ThreadId audio = threads.Create("Audio");
        bool ran = false;

        Task work = threads.InvokeAsync(audio, _ => ran = true, new CancellationToken(canceled: true));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        Assert.False(ran);
    }

    [Fact]
    public async Task Work_awaited_for_a_worker_is_handed_the_token()
    {
        using Threads threads = new();
        using CancellationTokenSource cancel = new();

        bool handed = await threads.InvokeAsync(ThreadId.Worker, token => token == cancel.Token, cancel.Token);

        Assert.True(handed);
    }

    [Fact]
    public async Task Work_on_a_dedicated_thread_that_stops_for_its_token_ends_cancelled()
    {
        using Threads threads = new();
        ThreadId audio = threads.Create("Audio");
        using CancellationTokenSource cancel = new();

        Task work = threads.InvokeAsync(audio, token =>
        {
            cancel.Cancel();
            token.ThrowIfCancellationRequested();
        }, cancel.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
    }

    [Fact]
    public void Work_posted_to_a_dedicated_thread_runs_in_the_order_it_was_posted()
    {
        using Threads threads = new();
        ThreadId audio = threads.Create("Audio");
        List<int> order = [];

        for (int i = 0; i < 5; i++)
        {
            int number = i;
            threads.Post(audio, () => order.Add(number));
        }

        threads.Invoke(audio, () => { }); // everything posted before it has run
        Assert.Equal([0, 1, 2, 3, 4], order);
    }

    [Fact]
    public void What_invoked_work_throws_is_thrown_to_the_caller()
    {
        using Threads threads = new();
        ThreadId audio = threads.Create("Audio");

        Action invoke = () => threads.Invoke(audio, () => throw new InvalidOperationException("on purpose"));

        Assert.Throws<InvalidOperationException>(invoke);
    }

    [Fact]
    public void A_dedicated_thread_is_current_inside_its_own_work()
    {
        using Threads threads = new();
        ThreadId audio = threads.Create("Audio");

        bool current = threads.Invoke(audio, () => threads.IsCurrent(audio));

        Assert.True(current);
    }

    [Fact]
    public void Work_for_a_thread_nothing_runs_yet_runs_on_the_calling_thread()
    {
        using Threads threads = new();
        int caller = Environment.CurrentManagedThreadId;

        int ran = threads.Invoke(ThreadId.Main, () => Environment.CurrentManagedThreadId);

        Assert.Equal(caller, ran);
    }

    [Fact]
    public void An_id_no_thread_has_is_refused()
    {
        using Threads threads = new();

        Action invoke = () => threads.Invoke(new ThreadId(99), () => { });

        Assert.Throws<ArgumentException>(invoke);
    }
}
