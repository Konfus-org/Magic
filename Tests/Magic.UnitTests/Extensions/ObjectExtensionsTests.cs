using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Extensions;
using System.Text.Json.Serialization;
using Xunit;

namespace Magic.UnitTests.Extensions;

public sealed class ObjectExtensionsTests
{
    [Fact]
    public void A_handle_in_a_property_is_found()
    {
        Material material = new() { Shader = new Handle<Shader>(5) };

        IEnumerable<object> found = material.ValuesOf<Handle<Shader>>();

        Assert.Equal([new Handle<Shader>(5)], found);
    }

    [Fact]
    public void A_handle_in_a_dictionary_value_is_found()
    {
        Material material = new() { Params = { ["color"] = Param.Of(new Handle<Texture>(6)) } };

        IEnumerable<object> found = material.ValuesOf<Handle<Texture>>();

        Assert.Equal([new Handle<Texture>(6)], found);
    }

    [Fact]
    public void A_handle_in_a_nested_struct_is_found()
    {
        Renderer renderer = new() { Materials = new MaterialSlots { Slot3 = new Handle<Material>(7) } };

        IEnumerable<object> found = renderer.ValuesOf<Handle<Material>>();

        Assert.Contains(new Handle<Material>(7), found);
    }

    [Fact]
    public void A_handle_in_an_array_is_found()
    {
        Holder holder = new() { Models = [new Handle<Model>(8), new Handle<Model>(9)] };

        IEnumerable<object> found = holder.ValuesOf<Handle<Model>>();

        Assert.Equal([new Handle<Model>(8), new Handle<Model>(9), new Handle<Model>(0)], found);
    }

    [Fact]
    public void A_handle_in_a_public_field_is_found()
    {
        Holder holder = new() { Field = new Handle<Model>(10) };

        IEnumerable<object> found = holder.ValuesOf<Handle<Model>>();

        Assert.Equal([new Handle<Model>(10)], found);
    }

    [Fact]
    public void A_handle_json_ignores_is_not_found()
    {
        Holder holder = new() { Ignored = new Handle<Model>(11) };

        IEnumerable<object> found = holder.ValuesOf<Handle<Model>>();

        Assert.DoesNotContain(new Handle<Model>(11), found);
    }

    [Fact]
    public void A_generic_target_finds_what_its_arguments_are_assignable_from()
    {
        Material material = new() { Shader = new Handle<Shader>(5) };

        IEnumerable<object> found = material.ValuesOf<Handle<Asset>>();

        Assert.Contains(new Handle<Shader>(5), found);
    }

    [Fact]
    public void A_generic_target_does_not_find_an_argument_it_is_not_assignable_from()
    {
        Holder holder = new() { Other = new Handle<string>(12) };

        IEnumerable<object> found = holder.ValuesOf<Handle<Asset>>();

        Assert.DoesNotContain(new Handle<string>(12), found);
    }

    [Fact]
    public void A_strict_target_finds_only_its_own_type()
    {
        Material material = new() { Shader = new Handle<Shader>(5) };

        IEnumerable<object> found = material.ValuesOf<Handle<Asset>>(strict: true);

        Assert.Empty(found);
    }

    [Fact]
    public void A_type_that_contains_itself_is_walked_without_going_round()
    {
        Node node = new() { Model = new Handle<Model>(13), Next = new Node { Model = new Handle<Model>(14) } };

        IEnumerable<object> found = node.ValuesOf<Handle<Model>>();

        Assert.Equal([new Handle<Model>(13)], found);
    }

    private sealed class Holder
    {
        public Handle<Model> Field;

        public Handle<Model>[] Models { get; set; } = [];

        [JsonIgnore]
        public Handle<Model> Ignored { get; set; }

        public Handle<string> Other { get; set; }
    }

    private sealed class Node
    {
        public Handle<Model> Model { get; set; }

        public Node? Next { get; set; }
    }
}
