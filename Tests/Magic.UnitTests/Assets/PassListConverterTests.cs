using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using System.Text.Json;
using Xunit;

namespace Magic.UnitTests.Assets;

public sealed class PassListConverterTests
{
    [Fact]
    public void An_array_of_handles_is_read_in_its_order()
    {
        const string json = """{ "passes": [ { "id": 30 }, { "id": 10 }, { "id": 20 } ] }""";

        PostProcessing component = JsonSerializer.Deserialize<PostProcessing>(json, AssetJson.Options);

        Assert.Equal([30ul, 10ul, 20ul], Ids(component.Passes));
    }

    [Fact]
    public void More_passes_than_fit_are_refused()
    {
        string handles = string.Join(", ", Enumerable.Range(1, PassList.Capacity + 1).Select(id => $$"""{ "id": {{id}} }"""));
        string json = $$"""{ "passes": [ {{handles}} ] }""";

        Action read = () => JsonSerializer.Deserialize<PostProcessing>(json, AssetJson.Options);

        Assert.Throws<JsonException>(read);
    }

    [Fact]
    public void Anything_but_an_array_is_refused()
    {
        const string json = """{ "passes": { "id": 30 } }""";

        Action read = () => JsonSerializer.Deserialize<PostProcessing>(json, AssetJson.Options);

        Assert.Throws<JsonException>(read);
    }

    [Fact]
    public void A_written_list_reads_back_the_same()
    {
        PassList list = default;
        list[0] = new Handle<Pass>(30);
        list[1] = new Handle<Pass>(10);
        string json = JsonSerializer.Serialize(new PostProcessing { Passes = list }, AssetJson.Options);

        PostProcessing component = JsonSerializer.Deserialize<PostProcessing>(json, AssetJson.Options);

        Assert.Equal([30ul, 10ul], Ids(component.Passes));
    }

    private static ulong[] Ids(PassList list)
    {
        ulong[] ids = new ulong[list.Count];
        for (int i = 0; i < ids.Length; i++)
            ids[i] = list[i].Id;

        return ids;
    }
}
