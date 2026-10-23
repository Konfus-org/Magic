using Magic.Contexts.Assets;
using Magic.Services;

namespace Magic.Tests.Services;

public sealed class AppTests
{
    [Fact]
    public void Quit_ends_the_run()
    {
        using App app = new(new Project { Name = "Test", EntryPoint = Handle<Domain>.None }, "Test");

        app.Quit("the test is done");

        Assert.True(app.IsQuitting);
    }

    [Fact]
    public void A_token_taken_before_the_quit_is_cancelled_by_it()
    {
        using App app = new(new Project { Name = "Test", EntryPoint = Handle<Domain>.None }, "Test");
        CancellationToken quitting = app.Quitting;

        app.Quit("the test is done");

        Assert.True(quitting.IsCancellationRequested);
    }

    [Fact]
    public void Quit_is_not_a_restart()
    {
        using App app = new(new Project { Name = "Test", EntryPoint = Handle<Domain>.None }, "Test");

        app.Quit("the test is done");

        Assert.False(app.IsRestarting);
    }

    [Fact]
    public void Restart_ends_the_run()
    {
        using App app = new(new Project { Name = "Test", EntryPoint = Handle<Domain>.None }, "Test");

        app.Restart("a setting changed");

        Assert.True(app.IsQuitting);
    }

    [Fact]
    public void Restart_ends_the_run_to_start_it_again()
    {
        using App app = new(new Project { Name = "Test", EntryPoint = Handle<Domain>.None }, "Test");

        app.Restart("a setting changed");

        Assert.True(app.IsRestarting);
    }

    [Fact]
    public void A_restart_once_the_app_is_quitting_changes_nothing()
    {
        using App app = new(new Project { Name = "Test", EntryPoint = Handle<Domain>.None }, "Test");
        app.Quit("the test is done");

        app.Restart("a setting changed");

        Assert.False(app.IsRestarting);
    }
}
