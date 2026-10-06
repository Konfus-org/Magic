using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Services;
using StreamingGem;
using Magic.Utils;
using System.Numerics;
using Xunit;

namespace Magic.IntegrationTests.Systems;

/// <summary>
/// The stand-in the chunk LOD generator makes of a chunk, asked for the way the streaming system does: as the chunk's
/// LOD from the asset service, over a temp Assets folder and a temp cache. The chunk's model is a 20 m cube in the
/// engine's own model form, read back by a loader that does only that.
/// </summary>
public sealed class ChunkLodsTests : IDisposable
{
    private static readonly Handle<Chunk> Cube = new(900);

    private readonly TempFolder _root = new();
    private Services.Assets? _assets;

    public ChunkLodsTests()
    {
        string model = _root.Write("Assets/Block.model", "");
        File.WriteAllBytes(model, Block().ToBytes());
        _root.Write("Assets/Block.model.meta", """{ "id": 700 }""");
    }

    public void Dispose()
    {
        _assets?.Dispose();
        _root.Dispose();
    }

    [Fact]
    public void A_big_static_object_is_drawn_by_the_stand_in()
    {
        WriteChunk("""{ "tags": [ "static" ], "components": { "Transform": {}, "Renderer": { "model": { "id": 700 } } } }""");

        Chunk standIn = StandIn();

        Assert.Single(standIn.Entities);
    }

    [Fact]
    public void The_stand_ins_model_holds_the_object_where_the_chunk_put_it()
    {
        WriteChunk("""{ "tags": [ "static" ], "components": { "Transform": { "position": { "x": 100, "y": 0, "z": 0 } }, "Renderer": { "model": { "id": 700 } } } }""");

        Model model = ModelOf(StandIn());

        Assert.Equal(100f, model.Meshes[0].Box.Center.X, 1e-3f);
    }

    [Fact]
    public void An_object_under_a_parent_is_placed_by_both_transforms()
    {
        WriteChunk("""
            { "components": { "Transform": { "position": { "x": 100, "y": 0, "z": 0 } } },
              "children": [ { "tags": [ "static" ], "components": { "Transform": { "position": { "x": 0, "y": 50, "z": 0 } }, "Renderer": { "model": { "id": 700 } } } } ] }
            """);

        Model model = ModelOf(StandIn());

        Assert.Equal(new Vector3(100, 50, 0), model.Meshes[0].Box.Center);
    }

    [Fact]
    public void An_object_that_is_not_static_is_left_out()
    {
        WriteChunk("""{ "components": { "Transform": {}, "Renderer": { "model": { "id": 700 } } } }""");

        Chunk standIn = StandIn();

        Assert.Empty(standIn.Entities);
    }

    [Fact]
    public void Objects_of_different_sizes_are_drawn_apart()
    {
        WriteChunk("""
            { "tags": [ "static" ], "components": { "Transform": {}, "Renderer": { "model": { "id": 700 } } } },
            { "tags": [ "static" ], "components": { "Transform": { "scale": { "x": 0.01, "y": 0.01, "z": 0.01 } }, "Renderer": { "model": { "id": 700 } } } }
            """);

        Chunk standIn = StandIn();

        Assert.Equal(2, standIn.Entities.Length);
    }

    [Fact]
    public void Objects_of_one_size_are_drawn_together()
    {
        WriteChunk("""
            { "tags": [ "static" ], "components": { "Transform": {}, "Renderer": { "model": { "id": 700 } } } },
            { "tags": [ "static" ], "components": { "Transform": { "position": { "x": 30, "y": 0, "z": 0 } }, "Renderer": { "model": { "id": 700 } } } }
            """);

        Chunk standIn = StandIn();

        Assert.Single(standIn.Entities);
    }

    [Fact]
    public void A_stand_in_entity_is_culled_by_the_size_of_its_objects()
    {
        WriteChunk("""{ "tags": [ "static" ], "components": { "Transform": {}, "Renderer": { "model": { "id": 700 } } } }""");

        Chunk standIn = StandIn();

        // The block's corners are 17.3 m from its centre.
        Assert.Equal(MathF.Sqrt(300f), standIn.Entities[0].Components["Renderer"].GetProperty("cullRadius").GetSingle(), 1e-3f);
    }

    [Fact]
    public void A_small_object_is_drawn_as_a_box_without_normals()
    {
        WriteChunk("""{ "tags": [ "static" ], "components": { "Transform": { "scale": { "x": 0.01, "y": 0.01, "z": 0.01 } }, "Renderer": { "model": { "id": 700 } } } }""");

        Model model = ModelOf(StandIn());

        Assert.All(model.Meshes[0].Vertices, vertex => Assert.Equal(Vector3.Zero, vertex.Normal));
    }

    [Theory]
    [InlineData("PointLight")]
    [InlineData("SpotLight")]
    [InlineData("AreaLight")]
    public void A_light_is_a_point_light_of_the_stand_in(string light)
    {
        WriteChunk($$"""{ "components": { "Transform": {}, "{{light}}": { "color": { "r": 1, "g": 1, "b": 1 }, "intensity": 1, "range": 5, "outerAngle": 1 } } }""");

        Chunk standIn = StandIn();

        Assert.Single(With(standIn, "PointLight"));
    }

    [Fact]
    public void Lights_standing_together_are_merged_into_one()
    {
        WriteChunk("""
            { "components": { "Transform": { "position": { "x": 10, "y": 0, "z": 10 } }, "PointLight": { "color": { "r": 1, "g": 0, "b": 0 }, "intensity": 1, "range": 5 } } },
            { "components": { "Transform": { "position": { "x": 10, "y": 4, "z": 10 } }, "PointLight": { "color": { "r": 0, "g": 1, "b": 0 }, "intensity": 1, "range": 5 } } }
            """);

        Chunk standIn = StandIn();

        Assert.Single(With(standIn, "PointLight"));
    }

    [Fact]
    public void Lights_standing_apart_are_each_a_glow_of_the_stand_in()
    {
        WriteChunk("""
            { "components": { "Transform": { "position": { "x": 10, "y": 0, "z": 10 } }, "PointLight": { "color": { "r": 1, "g": 0, "b": 0 }, "intensity": 1, "range": 5 } } },
            { "components": { "Transform": { "position": { "x": 40, "y": 0, "z": 10 } }, "SpotLight": { "color": { "r": 0, "g": 1, "b": 0 }, "intensity": 1, "range": 5, "outerAngle": 1 } } }
            """);

        Chunk standIn = StandIn();

        Assert.Equal(2, With(standIn, "Glow").Length);
    }

    [Fact]
    public void Lights_standing_close_together_share_one_glow()
    {
        WriteChunk("""
            { "components": { "Transform": { "position": { "x": 10, "y": 1, "z": 10 } }, "PointLight": { "color": { "r": 1, "g": 0, "b": 0 }, "intensity": 1, "range": 5 } } },
            { "components": { "Transform": { "position": { "x": 12, "y": 1, "z": 10 } }, "PointLight": { "color": { "r": 0, "g": 1, "b": 0 }, "intensity": 1, "range": 5 } } }
            """);

        Chunk standIn = StandIn();

        Assert.Single(With(standIn, "Glow"));
    }

    [Fact]
    public void A_glow_is_twice_as_big_for_four_times_the_light()
    {
        WriteChunk("""
            { "components": { "Transform": { "position": { "x": 10, "y": 0, "z": 10 } }, "PointLight": { "color": { "r": 1, "g": 1, "b": 1 }, "intensity": 4, "range": 5 } } },
            { "components": { "Transform": { "position": { "x": 40, "y": 0, "z": 10 } }, "PointLight": { "color": { "r": 1, "g": 1, "b": 1 }, "intensity": 16, "range": 5 } } }
            """);

        float[] radii = [.. With(StandIn(), "Glow").Select(glow => glow.Components["Glow"].GetProperty("radius").GetSingle())];

        Assert.Equal(radii[0] * 2f, radii[1], 1e-4f);
    }

    [Fact]
    public void A_merged_light_reaches_as_far_as_the_lights_it_stands_for()
    {
        WriteChunk("""
            { "components": { "Transform": { "position": { "x": 10, "y": 0, "z": 10 } }, "PointLight": { "color": { "r": 1, "g": 1, "b": 1 }, "intensity": 1, "range": 5 } } },
            { "components": { "Transform": { "position": { "x": 10, "y": 4, "z": 10 } }, "PointLight": { "color": { "r": 1, "g": 1, "b": 1 }, "intensity": 1, "range": 5 } } }
            """);

        Chunk standIn = StandIn();

        // Two metres from the middle to each, and their five.
        Assert.Equal(7f, With(standIn, "PointLight")[0].Components["PointLight"].GetProperty("range").GetSingle(), 1e-3f);
    }

    [Fact]
    public void A_light_that_gives_no_light_is_left_out()
    {
        WriteChunk("""{ "components": { "Transform": {}, "PointLight": { "intensity": 0, "range": 5 } } }""");

        Chunk standIn = StandIn();

        Assert.Empty(standIn.Entities);
    }

    [Fact]
    public void A_kept_light_is_where_the_chunk_put_it()
    {
        WriteChunk("""
            { "components": { "Transform": { "position": { "x": 100, "y": 0, "z": 0 } } },
              "children": [ { "components": { "Transform": { "position": { "x": 0, "y": 50, "z": 0 } }, "PointLight": { "color": { "r": 1, "g": 1, "b": 1 }, "intensity": 1, "range": 5 } } } ] }
            """);

        Chunk standIn = StandIn();

        Assert.Equal(50f, With(standIn, "PointLight")[0].Components["Transform"].GetProperty("position").GetProperty("y").GetSingle());
    }

    [Fact]
    public void A_scripted_entity_is_left_out()
    {
        WriteChunk("""{ "name": "Door", "scripts": [ { "id": 1 } ], "components": { "Transform": {} } }""");

        Chunk standIn = StandIn();

        Assert.Empty(standIn.Entities);
    }

    /// <summary>
    /// The stand-in's entities that carry the component.
    /// </summary>
    private static Chunk.Entity[] With(Chunk standIn, string component)
    {
        return [.. standIn.Entities.Where(entity => entity.Components.ContainsKey(component))];
    }

    private void WriteChunk(string entity)
    {
        _root.Write("Assets/0_0_0.chunk", $$"""{ "entities": [ {{entity}} ] }""");
        _root.Write("Assets/0_0_0.chunk.meta", """{ "id": 900 }""");
    }

    /// <summary>
    /// Opens the asset service over what was written, with the generator in its container, and asks it for the chunk's stand-in.
    /// </summary>
    private Chunk StandIn()
    {
        Project project = new() { Name = "Tests", Root = _root.Path, Resources = Path.Combine(_root.Path, "NoResources"), Cache = Path.Combine(_root.Path, "Cache") };
        Container container = new();
        container.Add<IAssetLoader<Model>>(new OwnForm());
        FileSystem files = new();
        _assets = new Services.Assets(project, files, new Events(), container, new Threads());
        container.Add<ILODGenerator<Chunk>>(new ChunkLods(_assets, files));

        return _assets.Load(new Handle<Chunk>(_assets.Lods(Cube).Levels[0].Asset)) ?? throw new InvalidOperationException("The stand-in did not load.");
    }

    /// <summary>
    /// The model the stand-in's first entity draws.
    /// </summary>
    private Model ModelOf(Chunk standIn)
    {
        ulong id = standIn.Entities[0].Components["Renderer"].GetProperty("model").GetProperty("id").GetUInt64();

        return _assets?.Load(new Handle<Model>(id)) ?? throw new InvalidOperationException("The model did not load.");
    }

    /// <summary>
    /// A cube 20 m a side around the origin, as 8 vertices.
    /// </summary>
    private static Model Block()
    {
        Vertex[] vertices = new Vertex[8];
        for (int i = 0; i < 8; i++)
            vertices[i] = new Vertex { Position = new Vector3((i & 1) == 0 ? -10 : 10, (i & 2) == 0 ? -10 : 10, (i & 4) == 0 ? -10 : 10), Normal = Vector3.UnitY };

        return new Model
        {
            Meshes = [new Mesh { Vertices = vertices, Indices = [0, 2, 3, 0, 3, 1, 4, 5, 7, 4, 7, 6, 0, 1, 5, 0, 5, 4, 2, 6, 7, 2, 7, 3, 0, 4, 6, 0, 6, 2, 1, 3, 7, 1, 7, 5] }],
            Parts = [new ModelPart(0, 0)],
            SlotNames = [""],
        };
    }

    /// <summary>
    /// The model gem's part, for models in the engine's own form only.
    /// </summary>
    private sealed class OwnForm : IAssetLoader<Model>
    {
        public Result Load(Model asset, byte[] bytes)
        {
            return asset.Read(bytes);
        }
    }
}
