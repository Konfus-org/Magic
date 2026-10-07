using System.Diagnostics.CodeAnalysis;
using Magic.Extensions;
using Magic.Interfaces;
using Xunit;

namespace MagicTests;

public sealed class TypeExtensionsTests
{
    [Fact]
    public void Create_uses_the_constructor_with_the_most_parameters()
    {
        Services container = new();
        container.Add("from the container");

        Wanting made = (Wanting)typeof(Wanting).Create(container);

        Assert.Equal("from the container", made.Text);
    }

    [Fact]
    public void An_extra_is_used_before_the_container()
    {
        Services container = new();
        container.Add("from the container");

        Wanting made = (Wanting)typeof(Wanting).Create(container, "extra");

        Assert.Equal("extra", made.Text);
    }

    [Fact]
    public void A_parameter_nothing_provides_throws()
    {
        Services container = new();

        Assert.Throws<InvalidOperationException>(() => typeof(Wanting).Create(container));
    }

    [Fact]
    public void A_nullable_parameter_nothing_provides_is_null()
    {
        Services container = new();

        Hoping made = (Hoping)typeof(Hoping).Create(container);

        Assert.Null(made.Text);
    }

    [Fact]
    public void A_nullable_parameter_something_provides_is_that()
    {
        Services container = new();
        container.Add("from the container");

        Hoping made = (Hoping)typeof(Hoping).Create(container);

        Assert.Equal("from the container", made.Text);
    }

    [Fact]
    public void An_array_of_a_core_interface_is_every_provider()
    {
        Services container = new();
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
        Services container = new();

        Gathering made = (Gathering)typeof(Gathering).Create(container);

        Assert.Empty(made.Gems);
    }

    [Fact]
    public void A_type_without_a_public_constructor_throws()
    {
        Services container = new();

        Assert.Throws<InvalidOperationException>(() => typeof(Closed).Create(container));
    }

    [Fact]
    public void What_the_constructor_throws_comes_out_as_it_was()
    {
        Services container = new();

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

    /// <summary>
    /// The least an <see cref="IServices"/> can be: instances by contract, in the order added.
    /// </summary>
    private sealed class Services : IServices
    {
        private readonly Dictionary<Type, List<object>> _byContract = [];

        public void Add(Type contract, object instance)
        {
            if (!_byContract.TryGetValue(contract, out List<object>? instances))
                _byContract[contract] = instances = [];

            instances.Add(instance);
        }

        public void Remove(Type contract, object instance)
        {
            if (_byContract.TryGetValue(contract, out List<object>? instances))
                instances.Remove(instance);
        }

        public bool Has(Type contract)
        {
            return _byContract.TryGetValue(contract, out List<object>? instances) && instances.Count > 0;
        }

        public object Get(Type contract)
        {
            return TryGet(contract, out object? instance) ? instance : throw new InvalidOperationException($"No {contract.Name}.");
        }

        public bool TryGet(Type contract, [NotNullWhen(true)] out object? instance)
        {
            instance = Has(contract) ? _byContract[contract][0] : null;
            return instance is not null;
        }

        public IReadOnlyList<object> All(Type contract)
        {
            return _byContract.TryGetValue(contract, out List<object>? instances) ? instances : [];
        }
    }
}
