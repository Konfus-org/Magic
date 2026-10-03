using Magic.Utils;
using Xunit;

namespace Magic.UnitTests.Utils;

public sealed class PoolTests
{
    [Fact]
    public void Renting_from_an_empty_pool_makes_one()
    {
        Pool<object> pool = new(() => new object());

        object rented = pool.Rent();

        Assert.NotNull(rented);
    }

    [Fact]
    public void A_returned_object_is_rented_again()
    {
        Pool<object> pool = new(() => new object());
        object first = pool.Rent();
        pool.Return(first);

        object second = pool.Rent();

        Assert.Same(first, second);
    }

    [Fact]
    public void A_returned_object_is_reset_first()
    {
        Pool<List<int>> pool = new(() => [], reset: list => list.Clear());
        List<int> list = pool.Rent();
        list.Add(1);

        pool.Return(list);

        Assert.Empty(pool.Rent());
    }

    [Fact]
    public void Returns_past_what_is_kept_are_dropped()
    {
        Pool<object> pool = new(() => new object(), keep: 1);
        pool.Return(new object());

        pool.Return(new object());

        Assert.Equal(1, pool.Idle);
    }

    [Fact]
    public void A_rented_object_is_not_handed_out_twice()
    {
        Pool<object> pool = new(() => new object());
        object first = pool.Rent();

        object second = pool.Rent();

        Assert.NotSame(first, second);
    }
}
