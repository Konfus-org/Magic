using Magic.Contexts.Settings;
using Magic.Services;
using Xunit;

namespace RandomnessTests;

public sealed class RandomnessTests
{
    [Fact]
    public void The_same_seed_and_purpose_repeat_the_numbers()
    {
        Randomness first = new(new RandomSettings { Seed = 7 });
        Randomness second = new(new RandomSettings { Seed = 7 });

        int[] a = [.. Enumerable.Range(0, 8).Select(_ => first.For("City").Next())];
        int[] b = [.. Enumerable.Range(0, 8).Select(_ => second.For("City").Next())];

        Assert.Equal(a, b);
    }

    [Fact]
    public void Different_purposes_get_different_numbers()
    {
        Randomness randomness = new(new RandomSettings { Seed = 7 });

        int city = randomness.For("City").Next();
        int cave = randomness.For("Cave").Next();

        Assert.NotEqual(city, cave);
    }

    [Fact]
    public void A_set_seed_is_the_seed()
    {
        Randomness randomness = new(new RandomSettings { Seed = 42 });

        int seed = randomness.Seed;

        Assert.Equal(42, seed);
    }

    [Fact]
    public void An_unset_seed_is_rolled_and_never_zero()
    {
        Randomness randomness = new(new RandomSettings());

        int seed = randomness.Seed;

        Assert.NotEqual(0, seed);
    }

    [Fact]
    public void Setting_the_seed_while_running_changes_what_is_made_next()
    {
        RandomSettings settings = new() { Seed = 7 };
        Randomness randomness = new(settings);
        int before = randomness.For("City").Next();

        settings.Seed = 42;
        int after = randomness.For("City").Next();

        Assert.NotEqual(before, after);
    }
}
