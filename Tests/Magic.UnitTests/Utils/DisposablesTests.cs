using Magic.Utils;
using Xunit;

namespace Magic.UnitTests.Utils;

public sealed class DisposablesTests
{
    [Fact]
    public void Disposing_disposes_every_item_in_the_order_given()
    {
        List<string> disposed = [];
        Disposables<Noted> owned = new(new Noted("first", disposed), new Noted("second", disposed));

        owned.Dispose();

        Assert.Equal(["first", "second"], disposed);
    }

    [Fact]
    public void An_added_item_is_disposed_with_the_rest()
    {
        List<string> disposed = [];
        Disposables<Noted> owned = new(new Noted("first", disposed));
        owned.Add(new Noted("added", disposed));

        owned.Dispose();

        Assert.Equal(["first", "added"], disposed);
    }

    [Fact]
    public void Adding_answers_the_item()
    {
        using Disposables<Noted> owned = new();
        Noted item = new("added", []);

        Noted answered = owned.Add(item);

        Assert.Same(item, answered);
    }

    [Fact]
    public void Removing_an_item_disposes_it_at_once()
    {
        List<string> disposed = [];
        using Disposables<Noted> owned = new();
        Noted item = owned.Add(new Noted("removed", disposed));

        owned.Remove(item);

        Assert.Equal(["removed"], disposed);
    }

    [Fact]
    public void A_removed_item_is_not_disposed_again_with_the_rest()
    {
        List<string> disposed = [];
        Disposables<Noted> owned = new();
        Noted item = owned.Add(new Noted("removed", disposed));
        owned.Remove(item);

        owned.Dispose();

        Assert.Single(disposed);
    }

    [Fact]
    public void Removing_what_is_not_there_is_refused()
    {
        using Disposables<Noted> owned = new();
        using Noted stranger = new("stranger", []);

        bool removed = owned.Remove(stranger);

        Assert.False(removed);
    }

    [Fact]
    public void Disposing_twice_disposes_each_item_once()
    {
        List<string> disposed = [];
        Disposables<Noted> owned = new(new Noted("only", disposed));
        owned.Dispose();

        owned.Dispose();

        Assert.Single(disposed);
    }

    /// <summary>
    /// Notes its name in <paramref name="disposed"/> when it is disposed.
    /// </summary>
    private sealed class Noted(string name, List<string> disposed) : IDisposable
    {
        public void Dispose()
        {
            disposed.Add(name);
        }
    }
}
