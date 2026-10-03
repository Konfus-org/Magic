using Magic.Contexts.Assets;
using System.Numerics;
using Xunit;

namespace Magic.UnitTests.Assets;

public sealed class ModelTests
{
    [Fact]
    public void A_model_read_from_its_own_form_has_the_vertices_it_was_written_with()
    {
        byte[] bytes = Triangle().ToBytes();
        Model read = new();

        read.Read(bytes);

        Assert.Equal(new Vector3(0, 2, 0), read.Meshes[0].Vertices[2].Position);
    }

    [Fact]
    public void A_model_read_from_its_own_form_has_the_indices_it_was_written_with()
    {
        byte[] bytes = Triangle().ToBytes();
        Model read = new();

        read.Read(bytes);

        Assert.Equal([0u, 1u, 2u], read.Meshes[0].Indices);
    }

    [Fact]
    public void A_model_read_from_its_own_form_has_the_parts_it_was_written_with()
    {
        byte[] bytes = Triangle().ToBytes();
        Model read = new();

        read.Read(bytes);

        Assert.Equal([new ModelPart(0, 3)], read.Parts);
    }

    [Fact]
    public void A_model_read_from_its_own_form_has_the_slot_names_it_was_written_with()
    {
        byte[] bytes = Triangle().ToBytes();
        Model read = new();

        read.Read(bytes);

        Assert.Equal(["Skin"], read.SlotNames);
    }

    [Fact]
    public void A_model_read_from_its_own_form_has_its_bounds()
    {
        byte[] bytes = Triangle().ToBytes();
        Model read = new();

        read.Read(bytes);

        Assert.Equal(new Vector3(1, 2, 0), read.Meshes[0].Box.Max);
    }

    [Fact]
    public void Bytes_of_another_format_are_not_read()
    {
        Model read = new();

        bool failed = read.Read([1, 2, 3, 4]).Failed;

        Assert.True(failed);
    }

    [Fact]
    public void A_file_cut_short_is_not_read()
    {
        byte[] bytes = Triangle().ToBytes();
        Model read = new();

        bool failed = read.Read(bytes[..^20]).Failed;

        Assert.True(failed);
    }

    private static Model Triangle()
    {
        return new Model
        {
            Meshes =
            [
                new Mesh
                {
                    Vertices =
                    [
                        new Vertex { Position = new Vector3(-1, 0, 0) },
                        new Vertex { Position = new Vector3(1, 0, 0) },
                        new Vertex { Position = new Vector3(0, 2, 0) },
                    ],
                    Indices = [0, 1, 2],
                },
            ],
            Parts = [new ModelPart(0, 3)],
            SlotNames = ["Skin"],
        };
    }
}
