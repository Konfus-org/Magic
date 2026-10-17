using Magic.Contexts.Utils;

namespace Magic.Tests.Contexts.Utils;

public sealed class ResultTests
{
    [Fact]
    public void A_failure_is_not_ok()
    {
        Result result = Result.Failure("the disk is full.");

        Assert.False(result.Ok);
    }

    [Fact]
    public void A_failure_says_why()
    {
        Result result = Result.Failure("the disk is full.");

        Assert.Equal("the disk is full.", result.Error);
    }

    [Fact]
    public void A_success_is_ok()
    {
        Result<int> result = Result.Success(7);

        Assert.True(result.Ok);
    }

    [Fact]
    public void A_success_carries_its_value()
    {
        Result<int> result = Result.Success(7);

        Assert.Equal(7, result.Value);
    }

    [Fact]
    public void A_typed_failure_has_no_value()
    {
        Result<string> result = Result.Failure<string>("it is not there.");

        Assert.Null(result.Value);
    }
}
