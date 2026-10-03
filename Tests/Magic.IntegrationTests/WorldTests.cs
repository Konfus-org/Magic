using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Events;
using Magic.Services;
using Magic.Utils;
using Xunit;

namespace Magic.IntegrationTests;

/// <summary>
/// The world over domain files in a temp Assets folder: Cave and Town, and a second Cave in another folder, which is
/// named like the first. Nothing streams here, so an open domain stays loading.
/// </summary>
public sealed class WorldTests : IDisposable
{
    private static readonly Handle<Domain> Cave = new(1);
    private static readonly Handle<Domain> Town = new(2);
    private static readonly Handle<Domain> OtherCave = new(3);
    private static readonly Handle<Domain> Missing = new(9999);

    private readonly TempFolder _root = new();
    private readonly Events _events = new();
    private readonly Threads _threads = new();
    private readonly Magic.Services.Assets _assets;
    private readonly World _world;

    public WorldTests()
    {
        Write("Cave/Cave.domain", 1);
        Write("Town/Town.domain", 2);
        Write("Other/Cave.domain", 3);
        Project project = new() { Name = "Tests", Root = _root.Path, Resources = Path.Combine(_root.Path, "NoResources") };
        _assets = new Magic.Services.Assets(project, new FileSystem(), _events, new Container(), _threads);
        _world = new World(_events, _assets, _threads);
    }

    public void Dispose()
    {
        _assets.Dispose();
        _threads.Dispose();
        _root.Dispose();
    }

    [Fact]
    public void Opening_a_domain_succeeds()
    {
        Result opened = _world.Open(Cave);

        Assert.True(opened.Ok);
    }

    [Fact]
    public void A_domain_opened_in_place_of_the_others_has_the_loading_domain_opened_before_it()
    {
        _world.Loading = Town;

        _world.Open(Cave);

        Assert.Equal([Town, Cave], _world.Active.Select(open => open.Domain));
    }

    [Fact]
    public void A_domain_opened_beside_the_others_has_no_loading_domain_opened()
    {
        _world.Loading = Town;

        _world.Open(Cave, OpenMode.Additive);

        Assert.Equal([Cave], _world.Active.Select(open => open.Domain));
    }

    [Fact]
    public void A_loading_domain_that_cannot_be_loaded_is_done_without()
    {
        _world.Loading = Missing;

        _world.Open(Cave);

        Assert.Equal([Cave], _world.Active.Select(open => open.Domain));
    }

    [Fact]
    public void An_opened_domain_has_no_progress_yet()
    {
        _world.Open(Cave);

        Assert.Equal(0f, _world.Active[0].Progress);
    }

    [Fact]
    public void An_opened_domain_is_active()
    {
        _world.Open(Cave);

        Assert.Equal([Cave], _world.Active.Select(open => open.Domain));
    }

    [Fact]
    public void An_opened_domain_is_loading()
    {
        _world.Open(Cave);

        Assert.Equal(DomainState.Loading, _world.StateOf(Cave));
    }

    [Fact]
    public void A_domain_that_was_never_opened_is_closed()
    {
        DomainState state = _world.StateOf(Cave);

        Assert.Equal(DomainState.Closed, state);
    }

    [Fact]
    public void Opening_a_domain_that_is_not_an_asset_fails()
    {
        Result opened = _world.Open(Missing);

        Assert.True(opened.Failed);
    }

    [Fact]
    public void A_domain_that_could_not_be_opened_is_not_active()
    {
        _world.Open(Missing);

        Assert.Empty(_world.Active);
    }

    [Fact]
    public void A_failed_open_leaves_the_open_domains()
    {
        _world.Open(Cave);

        _world.Open(Missing);

        Assert.Equal([Cave], _world.Active.Select(open => open.Domain));
    }

    [Fact]
    public void Opening_a_domain_closes_the_open_ones()
    {
        _world.Open(Cave);

        _world.Open(Town);

        Assert.Equal([Town], _world.Active.Select(open => open.Domain));
    }

    [Fact]
    public void An_additive_open_keeps_the_open_ones()
    {
        _world.Open(Cave);

        _world.Open(Town, OpenMode.Additive);

        Assert.Equal([Cave, Town], _world.Active.Select(open => open.Domain));
    }

    [Fact]
    public void An_additive_open_of_an_open_domain_keeps_it_open_once()
    {
        _world.Open(Cave);

        _world.Open(Cave, OpenMode.Additive);

        Assert.Equal([Cave], _world.Active.Select(open => open.Domain));
    }

    [Fact]
    public void An_additive_open_of_a_domain_named_like_an_open_one_fails()
    {
        _world.Open(Cave);

        Result opened = _world.Open(OtherCave, OpenMode.Additive);

        Assert.True(opened.Failed);
    }

    [Fact]
    public void A_domain_named_like_an_open_one_replaces_it()
    {
        _world.Open(Cave);

        _world.Open(OtherCave);

        Assert.Equal([OtherCave], _world.Active.Select(open => open.Domain));
    }

    [Fact]
    public void Closing_a_domain_leaves_the_others_open()
    {
        _world.Open(Cave);
        _world.Open(Town, OpenMode.Additive);

        _world.Close(Cave);

        Assert.Equal([Town], _world.Active.Select(open => open.Domain));
    }

    [Fact]
    public void A_closed_domain_is_closed()
    {
        _world.Open(Cave);

        _world.Close(Cave);

        Assert.Equal(DomainState.Closed, _world.StateOf(Cave));
    }

    [Fact]
    public void Closing_a_domain_that_is_not_open_leaves_the_open_ones()
    {
        _world.Open(Cave);

        _world.Close(Town);

        Assert.Equal([Cave], _world.Active.Select(open => open.Domain));
    }

    [Fact]
    public void Opening_the_void_closes_every_domain()
    {
        _world.Open(Cave);
        _world.Open(Town, OpenMode.Additive);

        _world.Open(Handle<Domain>.None);

        Assert.Empty(_world.Active);
    }

    [Fact]
    public async Task A_domain_opened_asynchronously_is_active()
    {
        await _world.OpenAsync(Cave);

        Assert.Equal([Cave], _world.Active.Select(open => open.Domain));
    }

    [Fact]
    public async Task Opening_asynchronously_a_domain_that_is_not_an_asset_fails()
    {
        Result opened = await _world.OpenAsync(Missing);

        Assert.True(opened.Failed);
    }

    [Fact]
    public async Task A_cancelled_asynchronous_open_throws()
    {
        using CancellationTokenSource cancel = new();
        await cancel.CancelAsync();

        Func<Task> open = () => _world.OpenAsync(Cave, cancel: cancel.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(open);
    }

    [Fact]
    public void Ending_the_world_publishes_quit()
    {
        _world.End();

        Assert.Equal([new Event(EventType.Quit)], _events.NextFrame());
    }

    private void Write(string file, ulong id)
    {
        _root.Write($"Assets/{file}", "{}");
        _root.Write($"Assets/{file}.meta", $$"""{ "id": {{id}}, "version": 1 }""");
    }
}
