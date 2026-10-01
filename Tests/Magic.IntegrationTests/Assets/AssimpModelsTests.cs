using AssimpGem;
using Magic.Contexts.Assets;
using Magic.Services;
using System.Numerics;
using System.Text;
using Xunit;

namespace Magic.IntegrationTests.Assets;

/// <summary>The model loader over the real Assimp: what it does to bring a file into engine space.</summary>
public sealed class AssimpModelsTests
{
    // A right-handed triangle facing +Z, one metre on a side.
    private const string Triangle = "v 0 0 0\nv 1 0 0\nv 0 1 0\nvn 0 0 1\nvn 0 0 1\nvn 0 0 1\nf 1//1 2//2 3//3\n";

    [Fact]
    public void A_right_handed_model_is_mirrored_in_x()
    {
        Model model = new() { Path = "Tri.obj" };

        Loader().Load(model, Encoding.ASCII.GetBytes(Triangle));

        Assert.Contains(new Vector3(-1, 0, 0), model.Meshes[0].Vertices.Select(v => v.Position));
    }

    [Fact]
    public void Triangles_wind_clockwise_seen_from_outside()
    {
        Model model = new() { Path = "Tri.obj" };

        Loader().Load(model, Encoding.ASCII.GetBytes(Triangle));

        Mesh mesh = model.Meshes[0];
        Vertex first = mesh.Vertices[mesh.Indices[0]], second = mesh.Vertices[mesh.Indices[1]], third = mesh.Vertices[mesh.Indices[2]];
        Vector3 face = Vector3.Normalize(Vector3.Cross(second.Position - first.Position, third.Position - first.Position));
        Assert.Equal(1f, Vector3.Dot(face, first.Normal), 3);
    }

    [Fact]
    public void An_fbx_is_scaled_to_metres()
    {
        Model cube = new() { Path = "Models/Cube.fbx" };

        Loader().Load(cube, Resources.Read(cube.Path));

        Assert.All(cube.Meshes[0].Vertices, vertex => Assert.Equal(1f, MathF.Abs(vertex.Position.X), 3));
    }

    [Fact]
    public void A_z_up_model_becomes_y_up()
    {
        Model plane = new() { Path = "Models/Plane.fbx" };

        Loader().Load(plane, Resources.Read(plane.Path));

        Assert.All(plane.Meshes[0].Vertices, vertex => Assert.Equal(1f, vertex.Normal.Y, 3));
    }

    [Fact]
    public void Bytes_that_are_not_a_model_are_refused()
    {
        Model model = new() { Path = "Broken.fbx" };

        Action load = () => Loader().Load(model, [1, 2, 3]);

        Assert.ThrowsAny<Exception>(load);
    }

    private static AssimpModels Loader()
    {
        return new AssimpModels(new Project { EngineGems = AppContext.BaseDirectory });
    }
}
