using Magic.Contexts.Rendering;
using Magic.Utils;
using Xunit;

namespace Magic.UnitTests.Render;

public sealed class CompilesTests
{
    [Fact]
    public void A_finished_job_is_delivered()
    {
        Compiles<int> compiles = new();
        List<(int Key, string Message)> done = [];
        compiles.Start(1, Task.FromResult(Result<CompiledShader>.Failure("a")));

        compiles.Poll(done, Record);

        Assert.Equal([(1, "a")], done);
    }

    [Fact]
    public void A_delivered_job_is_not_delivered_again()
    {
        Compiles<int> compiles = new();
        List<(int Key, string Message)> done = [];
        compiles.Start(1, Task.FromResult(Result<CompiledShader>.Failure("a")));
        compiles.Poll(done, Record);

        compiles.Poll(done, Record);

        Assert.Single(done);
    }

    [Fact]
    public void A_running_job_is_not_delivered()
    {
        Compiles<int> compiles = new();
        List<(int Key, string Message)> done = [];
        compiles.Start(1, new TaskCompletionSource<Result<CompiledShader>>().Task);

        compiles.Poll(done, Record);

        Assert.Empty(done);
    }

    [Fact]
    public void A_superseded_job_is_never_delivered()
    {
        Compiles<int> compiles = new();
        List<(int Key, string Message)> done = [];
        compiles.Start(1, Task.FromResult(Result<CompiledShader>.Failure("stale")));
        compiles.Start(1, Task.FromResult(Result<CompiledShader>.Failure("newest")));

        compiles.Poll(done, Record);

        Assert.Equal([(1, "newest")], done);
    }

    [Fact]
    public void A_faulted_job_is_delivered_as_a_failure()
    {
        Compiles<int> compiles = new();
        List<(int Key, string Message)> done = [];
        compiles.Start(1, Task.FromException<Result<CompiledShader>>(new InvalidOperationException("boom")));

        compiles.Poll(done, Record);

        Assert.Equal([(1, "boom")], done);
    }

    private static void Record(List<(int Key, string Message)> done, int key, Result<CompiledShader> result)
    {
        done.Add((key, result.Message));
    }
}
