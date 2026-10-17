using Magic.Contexts.Assets;
using Magic.Extensions;

namespace Magic.Tests.Extensions;

public sealed class CurveExtensionsTests
{
    [Fact]
    public void At_is_zero_for_a_curve_with_no_keys()
    {
        Curve curve = new();

        float value = curve.At(5);

        Assert.Equal(0, value);
    }

    [Fact]
    public void At_holds_the_first_value_before_the_first_key()
    {
        Curve curve = new() { Keys = [new CurveKey(0, 1), new CurveKey(10, 3)] };

        float value = curve.At(-5);

        Assert.Equal(1, value);
    }

    [Fact]
    public void At_holds_the_last_value_after_the_last_key()
    {
        Curve curve = new() { Keys = [new CurveKey(0, 1), new CurveKey(10, 3)] };

        float value = curve.At(20);

        Assert.Equal(3, value);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(10, 3)]
    public void At_is_the_keys_value_on_a_key(float place, float expected)
    {
        Curve curve = new() { Keys = [new CurveKey(0, 1), new CurveKey(10, 3)] };

        float value = curve.At(place);

        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData(2.5f, 1.5f)]
    [InlineData(5, 2)]
    [InlineData(7.5f, 2.5f)]
    public void At_runs_straight_between_two_keys(float place, float expected)
    {
        Curve curve = new() { Keys = [new CurveKey(0, 1), new CurveKey(10, 3)] };

        float value = curve.At(place);

        Assert.Equal(expected, value);
    }

    [Fact]
    public void At_runs_from_the_later_of_two_keys_sharing_a_place()
    {
        Curve curve = new() { Keys = [new CurveKey(0, 1), new CurveKey(5, 2), new CurveKey(5, 4), new CurveKey(10, 6)] };

        float value = curve.At(7.5f);

        Assert.Equal(5, value);
    }
}
