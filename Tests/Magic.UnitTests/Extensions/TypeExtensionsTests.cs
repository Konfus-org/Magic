using Magic.Extensions;
using Magic.Interfaces;
using Magic.Services;
using Xunit;

namespace Magic.UnitTests.Extensions;

public sealed class TypeExtensionsTests
{
    [Fact]
    public void Create_uses_the_constructor_with_the_most_parameters()
    {
        Container container = new();
        container.Add("from the container");

        Wanting made = (Wanting)typeof(Wanting).Create(container);

        Assert.Equal("from the container", made.Text);
    }

    [Fact]
    public void An_extra_is_used_before_the_container()
    {
        Container container = new();
        container.Add("from the container");

        Wanting made = (Wanting)typeof(Wanting).Create(container, "extra");

        Assert.Equal("extra", made.Text);
    }

    [Fact]
    public void A_parameter_nothing_provides_throws()
    {
        Container container = new();

        Assert.Throws<InvalidOperationException>(() => typeof(Wanting).Create(container));
    }

    [Fact]
    public void A_nullable_parameter_nothing_provides_is_null()
    {
        Container container = new();

        Hoping made = (Hoping)typeof(Hoping).Create(container);

        Assert.Null(made.Text);
    }

    [Fact]
    public void A_nullable_parameter_something_provides_is_that()
    {
        Container container = new();
        container.Add("from the container");

        Hoping made = (Hoping)typeof(Hoping).Create(container);

        Assert.Equal("from the container", made.Text);
    }

    [Fact]
    public void An_array_of_a_core_interface_is_every_provider()
    {
        Container container = new();
        using Quiet first = new();
        using Quiet second = new();
        container.Add<IGem>(first);
        container.Add<IGem>(second);

        Gathering made = (Gathering)typeof(Gathering).Create(container);

        Assert.Equal(2, made.Gems.Length);
    }

    [Fact]
    public void An_array_of_a_core_interface_nothing_provides_is_empty()
    {
        Container container = new();

        Gathering made = (Gathering)typeof(Gathering).Create(container);

        Assert.Empty(made.Gems);
    }

    [Fact]
    public void A_type_without_a_public_constructor_throws()
    {
        Container container = new();

        Assert.Throws<InvalidOperationException>(() => typeof(Closed).Create(container));
    }

    [Fact]
    public void What_the_constructor_throws_comes_out_as_it_was()
    {
        Container container = new();

        Assert.Throws<NotSupportedException>(() => typeof(Refusing).Create(container));
    }

    private sealed class Wanting
    {
        public Wanting()
        {
        }

        public Wanting(string text)
        {
            Text = text;
        }

        public string? Text { get; }
    }

    private sealed class Hoping(string? text)
    {
        public string? Text { get; } = text;
    }

    private sealed class Gathering(IGem[] gems)
    {
        public IGem[] Gems { get; } = gems;
    }

    private sealed class Quiet : IGem;

    private sealed class Closed
    {
        private Closed()
        {
        }
    }

    private sealed class Refusing
    {
        public Refusing()
        {
            throw new NotSupportedException();
        }
    }
}
