using FlecsGem;
using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using StreamingGem;
using System.Numerics;
using System.Text;
using Xunit;

namespace Magic.IntegrationTests.Systems;

/// <summary>
/// A chunk file's bytes read into what the streaming system spawns; a component's value is seen by giving it to an entity of the real Flecs gem.
/// </summary>
public sealed class ChunkReaderTests
{
    [Fact]
    public void Every_entity_of_the_file_is_read()
    {
        PreparedChunk chunk = Read("""{ "entities": [ { "name": "A" }, { "name": "B" } ] }""");

        Assert.Equal(["A", "B"], chunk.Entities.Select(entity => entity.Name));
    }

    [Fact]
    public void An_entity_keeps_its_id()
    {
        PreparedChunk chunk = Read("""{ "entities": [ { "id": 7 } ] }""");

        Assert.Equal(7ul, chunk.Entities[0].Id);
    }

    [Fact]
    public void A_child_comes_after_its_parent_and_points_at_it()
    {
        PreparedChunk chunk = Read("""{ "entities": [ { "name": "Parent", "children": [ { "name": "Child" } ] }, { "name": "Next" } ] }""");

        Assert.Equal([-1, 0, -1], chunk.Entities.Select(entity => entity.Parent));
    }

    [Fact]
    public void A_component_is_read_as_the_struct_its_name_means()
    {
        PreparedChunk chunk = Read("""{ "entities": [ { "components": { "Transform": { "position": { "x": 1, "y": 2, "z": 3 } } } } ] }""");

        Assert.Equal(new Vector3(1, 2, 3), Given<Transform>(chunk, entity: 0).Position);
    }

    [Fact]
    public void Each_entity_gets_its_own_value_of_a_component()
    {
        PreparedChunk chunk = Read("""
            { "entities": [
                { "components": { "Transform": { "position": { "x": 1 } } } },
                { "components": { "Transform": { "position": { "x": 2 } } } }
            ] }
            """);

        Assert.Equal(2f, Given<Transform>(chunk, entity: 1).Position.X);
    }

    [Fact]
    public void A_parents_components_are_its_own_when_its_children_are_written_first()
    {
        PreparedChunk chunk = Read("""
            { "entities": [ {
                "children": [ { "components": { "Transform": { "position": { "x": 9 } } } } ],
                "components": { "Transform": { "position": { "x": 1 } } }
            } ] }
            """);

        Assert.Equal(1f, Given<Transform>(chunk, entity: 0).Position.X);
    }

    [Fact]
    public void A_component_declared_outside_core_is_found_by_name()
    {
        PreparedChunk chunk = Read("""{ "entities": [ { "components": { "Spin": { "speed": 2 } } } ] }""");

        Assert.Equal(2f, Given<Spin>(chunk, entity: 0).Speed);
    }

    [Fact]
    public void A_name_no_component_has_is_skipped()
    {
        PreparedChunk chunk = Read("""{ "entities": [ { "components": { "Nope": { "a": [ 1, { "b": 2 } ] }, "Transform": {} } } ] }""");

        Assert.Equal(1, chunk.Entities[0].ComponentCount);
    }

    [Fact]
    public void A_component_that_cannot_be_read_is_skipped_and_the_rest_is_read()
    {
        PreparedChunk chunk = Read("""{ "entities": [ { "components": { "Transform": { "position": "nowhere" } } }, { "name": "After" } ] }""");

        Assert.Equal((0, "After"), (chunk.Entities[0].ComponentCount, chunk.Entities[1].Name));
    }

    [Fact]
    public void Tags_are_read_by_name()
    {
        PreparedChunk chunk = Read("""{ "entities": [ { "tags": [ "static" ] } ] }""");

        Assert.True(chunk.Entities[0].Tags?.Has(Tag.Static));
    }

    [Fact]
    public void An_entity_without_tags_has_none()
    {
        PreparedChunk chunk = Read("""{ "entities": [ { "name": "Plain" } ] }""");

        Assert.Null(chunk.Entities[0].Tags);
    }

    [Fact]
    public void Scripts_stay_as_the_file_has_them()
    {
        PreparedChunk chunk = Read("""{ "entities": [ { "scripts": [ { "id": 9, "speed": 3 } ] } ] }""");

        Assert.Equal(3, chunk.Entities[0].Scripts[0].GetProperty("speed").GetInt32());
    }

    [Fact]
    public void Property_names_are_matched_without_regard_to_case()
    {
        PreparedChunk chunk = Read("""{ "Entities": [ { "Name": "A" } ] }""");

        Assert.Equal("A", chunk.Entities[0].Name);
    }

    [Fact]
    public void A_byte_order_mark_is_not_part_of_the_file()
    {
        byte[] file = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("""{ "entities": [ { "name": "A" } ] }""")];

        PreparedChunk chunk = new ChunkReader().Read(new Chunk { Data = file });

        Assert.Single(chunk.Entities);
    }

    [Fact]
    public void Comments_and_trailing_commas_are_allowed()
    {
        PreparedChunk chunk = Read("""{ "entities": [ { "name": "A", }, /* the last one */ ] }""");

        Assert.Single(chunk.Entities);
    }

    [Fact]
    public void A_file_that_is_not_an_object_is_refused()
    {
        Action read = () => Read("[]");

        Assert.ThrowsAny<System.Text.Json.JsonException>(read);
    }

    private static PreparedChunk Read(string json)
    {
        return new ChunkReader().Read(new Chunk { Data = Encoding.UTF8.GetBytes(json) });
    }

    /// <summary>
    /// The value the chunk gives one of its entities for a component, as an entity given it then has it.
    /// </summary>
    private static T Given<T>(PreparedChunk chunk, int entity) where T : unmanaged
    {
        using FlecsEcs ecs = new();
        Handle handle = ecs.Create();
        PreparedEntity prepared = chunk.Entities[entity];
        for (int i = prepared.FirstComponent; i < prepared.FirstComponent + prepared.ComponentCount; i++)
            chunk.Columns[chunk.Components[i].Column].Set(ecs, handle, chunk.Components[i].Row);

        return ecs.Get<T>(handle);
    }
}
