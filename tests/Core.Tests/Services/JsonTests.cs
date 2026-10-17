using System.Text;
using System.Text.Json;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Contexts.Utils;
using Magic.Services;

namespace Magic.Tests.Services;

public sealed class JsonTests
{
    [Fact]
    public void Read_skips_a_byte_order_mark()
    {
        byte[] file = [.. Encoding.UTF8.Preamble, .. "{ \"cellSize\": 32 }"u8];

        Result<Domain> read = Json.Read<Domain>(file);

        Assert.Equal(32f, read.Value?.CellSize);
    }

    [Fact]
    public void Read_matches_a_key_to_its_property_in_camel_case()
    {
        Result<Domain> read = Json.Read<Domain>("{ \"cellSize\": 32 }"u8);

        Assert.Equal(32f, read.Value?.CellSize);
    }

    [Fact]
    public void Read_refuses_a_key_the_type_lacks()
    {
        Result<Domain> read = Json.Read<Domain>("{ \"cellsize\": 32 }"u8);

        Assert.False(read.Ok);
    }

    [Fact]
    public void Read_refuses_an_assets_handle()
    {
        Result<Domain> read = Json.Read<Domain>("{ \"handle\": { \"id\": 3 } }"u8);

        Assert.False(read.Ok);
    }

    [Fact]
    public void Read_refuses_null()
    {
        Result<Domain> read = Json.Read<Domain>("null"u8);

        Assert.False(read.Ok);
    }

    [Fact]
    public void Read_fails_rather_than_throws_on_broken_json()
    {
        Result<Domain> read = Json.Read<Domain>("{ \"cellSize\": "u8);

        Assert.False(read.Ok);
    }

    [Fact]
    public void Read_reads_an_enum_by_its_snake_case_name()
    {
        Result<CameraTarget> read = Json.Read<CameraTarget>("{ \"section\": \"top_left\" }"u8);

        Assert.Equal(ScreenSection.TopLeft, read.Value.Section);
    }

    [Fact]
    public void Read_reads_a_components_fields()
    {
        Result<Transform> read = Json.Read<Transform>("{ \"position\": { \"x\": 1, \"y\": 2, \"z\": 3 } }"u8);

        Assert.Equal(new Vector3D(1, 2, 3), read.Value.Position);
    }

    [Fact]
    public void Read_of_an_element_refuses_a_key_the_type_lacks()
    {
        using JsonDocument document = JsonDocument.Parse("{ \"cellsize\": 32 }");

        Result<Domain> read = Json.Read<Domain>(document.RootElement);

        Assert.False(read.Ok);
    }

    [Fact]
    public void Read_of_an_element_reads_the_type()
    {
        using JsonDocument document = JsonDocument.Parse("{ \"cellSize\": 32 }");

        Result<Domain> read = Json.Read<Domain>(document.RootElement);

        Assert.Equal(32f, read.Value?.CellSize);
    }
}
