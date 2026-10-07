using Magic.Services;
using Xunit;

namespace MagicTests;

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
}
