using Magic.Contexts.Threading;
using Magic.Services;
using Xunit;

namespace Magic.UnitTests.Services;

public sealed class MainThreadTests
{
    [Fact]
    public void Work_invoked_before_the_main_thread_runs_is_done_by_the_caller()
    {
        using Threads threads = new();
        MainThread main = new(threads);
        int caller = Environment.CurrentManagedThreadId;

        int ran = main.Invoke(() => Environment.CurrentManagedThreadId);

        Assert.Equal(caller, ran);
    }

    [Fact]
    public void Work_posted_from_another_thread_waits_for_the_main_thread_to_run_it()
    {
        using Threads threads = new();
        MainThread main = new(threads);
        threads.Claim(ThreadId.Main); // this thread is the main thread from here on
        bool ran = false;

        Thread other = new(() => main.Post(() => ran = true));
        other.Start();
        other.Join();

        Assert.False(ran);
    }

    [Fact]
    public void Posted_work_runs_when_the_main_thread_runs_what_it_was_handed()
    {
        using Threads threads = new();
        MainThread main = new(threads);
        threads.Claim(ThreadId.Main);
        bool ran = false;
        Thread other = new(() => main.Post(() => ran = true));
        other.Start();
        other.Join();

        threads.RunPending(ThreadId.Main);

        Assert.True(ran);
    }

    [Fact]
    public void The_thread_that_claims_main_is_the_main_thread()
    {
        using Threads threads = new();
        MainThread main = new(threads);

        threads.Claim(ThreadId.Main);

        Assert.True(main.IsCurrent);
    }

    [Fact]
    public async Task Another_thread_is_not_the_main_thread()
    {
        using Threads threads = new();
        MainThread main = new(threads);
        threads.Claim(ThreadId.Main);

        bool current = await Task.Run(() => main.IsCurrent);

        Assert.False(current);
    }
}
