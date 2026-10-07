using Magic.Contexts;
using Magic.Contexts.Assets;
using Xunit;

namespace WorldTests;

public sealed class ChunkTests
{
    [Theory]
    [InlineData("-1_0_3", -1, 0, 3)]
    [InlineData("0_0_0", 0, 0, 0)]
    public void A_name_of_three_integers_is_a_coordinate(string name, int x, int y, int z)
    {
        (int X, int Y, int Z)? coordinate = Chunk.ParseCoordinate(name);

        Assert.Equal((x, y, z), coordinate);
    }

    [Theory]
    [InlineData("globals")]
    [InlineData("1_2")]
    [InlineData("1_2_3_4")]
    [InlineData("a_b_c")]
    [InlineData("")]
    public void Any_other_name_is_not_a_coordinate(string name)
    {
        (int X, int Y, int Z)? coordinate = Chunk.ParseCoordinate(name);

        Assert.Null(coordinate);
    }

    [Fact]
    public void An_entity_finds_its_components_whatever_the_case_of_the_name()
    {
        Dictionary<string, Unread> caseSensitive = new() { ["Transform"] = default };

        Chunk.Entity entity = new() { Components = caseSensitive };

        Assert.True(entity.Components.ContainsKey("transform"));
    }
}
