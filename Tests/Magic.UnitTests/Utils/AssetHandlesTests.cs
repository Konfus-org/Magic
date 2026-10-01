using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Utils;
using System.Text.Json.Serialization;
using Xunit;

namespace Magic.UnitTests.Utils;

public sealed class AssetHandlesTests
{
    [Fact]
    public void A_handle_in_a_property_is_found()
    {
        Material material = new() { Shader = new Handle<Shader>(5) };

        List<(Type Type, ulong Id)> found = Find(material);

        Assert.Equal([(typeof(Shader), 5ul)], found);
    }

    [Fact]
    public void A_handle_in_a_dictionary_value_is_found()
    {
        Material material = new() { Params = { ["color"] = Param.Of(new Handle<Texture>(6)) } };

        List<(Type Type, ulong Id)> found = Find(material);

        Assert.Equal([(typeof(Texture), 6ul)], found);
    }

    [Fact]
    public void A_handle_in_a_nested_struct_is_found()
    {
        Renderer renderer = new() { Materials = new MaterialSlots { Slot3 = new Handle<Material>(7) } };

        List<(Type Type, ulong Id)> found = Find(renderer);

        Assert.Equal([(typeof(Material), 7ul)], found);
    }

    [Fact]
    public void A_handle_in_an_array_is_found()
    {
        Holder holder = new() { Models = [new Handle<Model>(8), new Handle<Model>(9)] };

        List<(Type Type, ulong Id)> found = Find(holder);

        Assert.Equal([(typeof(Model), 8ul), (typeof(Model), 9ul)], found);
    }

    [Fact]
    public void A_handle_in_a_public_field_is_found()
    {
        Holder holder = new() { Field = new Handle<Model>(10) };

        List<(Type Type, ulong Id)> found = Find(holder);

        Assert.Equal([(typeof(Model), 10ul)], found);
    }

    [Fact]
    public void A_handle_that_is_none_is_not_found()
    {
        List<(Type Type, ulong Id)> found = Find(new Renderer());

        Assert.Empty(found);
    }

    [Fact]
    public void A_handle_json_ignores_is_not_found()
    {
        Holder holder = new() { Ignored = new Handle<Model>(11) };

        List<(Type Type, ulong Id)> found = Find(holder);

        Assert.Empty(found);
    }

    [Fact]
    public void A_handle_to_something_that_is_not_an_asset_is_not_found()
    {
        Holder holder = new() { Other = new Handle<string>(12) };

        List<(Type Type, ulong Id)> found = Find(holder);

        Assert.Empty(found);
    }

    [Fact]
    public void A_type_that_contains_itself_is_walked_without_going_round()
    {
        Node node = new() { Model = new Handle<Model>(13), Next = new Node { Model = new Handle<Model>(14) } };

        List<(Type Type, ulong Id)> found = Find(node);

        Assert.Equal([(typeof(Model), 13ul)], found);
    }

    private static List<(Type Type, ulong Id)> Find(object holder)
    {
        List<(Type Type, ulong Id)> found = [];
        AssetHandles.Find(holder, found);

        return found;
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
