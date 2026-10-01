using Magic.Extensions;
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
