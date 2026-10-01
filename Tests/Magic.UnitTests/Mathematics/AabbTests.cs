using Magic.Extensions;
using Magic.Mathematics;
using System.Numerics;
using Xunit;

namespace Magic.UnitTests.Mathematics;

public sealed class AabbTests
{
    private static readonly Aabb UnitBox = new(new Vector3(-1, -1, -1), new Vector3(1, 1, 1));

    [Fact]
    public void A_transformed_box_is_centred_on_the_translation()
    {
        Matrix4x4 move = Matrix4x4.CreateTranslation(10, 0, 0);

        Aabb moved = UnitBox.Transform(move);

        Assert.Equal(new Vector3(10, 0, 0), moved.Center);
    }

    [Fact]
    public void A_transformed_box_grows_to_hold_its_rotated_corners()
    {
        Matrix4x4 eighthTurnAboutY = Matrix4x4.Trs(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 4f), Vector3.One);

        Aabb turned = UnitBox.Transform(eighthTurnAboutY);

        Assert.Equal(MathF.Sqrt(2f), turned.Extents.X, 1e-5f);
    }

    [Theory]
    [InlineData(0f, true)]
    [InlineData(1f, true)]    // on the face
    [InlineData(1.5f, false)]
    public void A_box_contains_points_up_to_its_faces(float x, bool expected)
    {
        bool contains = UnitBox.Contains(new Vector3(x, 0, 0));

        Assert.Equal(expected, contains);
    }

    [Fact]
    public void The_sphere_of_a_box_passes_through_its_corners()
    {
        BoundingSphere sphere = UnitBox.Sphere;

        Assert.Equal(MathF.Sqrt(3f), sphere.Radius, 1e-5f);
    }
}
