using Magic.Extensions;
using Magic.Mathematics;
using System.Numerics;
using Xunit;

namespace Magic.UnitTests.Mathematics;

public sealed class BoundingSphereTests
{
    [Fact]
    public void A_transformed_sphere_scales_by_the_longest_axis()
    {
        BoundingSphere unit = new(Vector3.Zero, 1f);
        Matrix4x4 stretched = Matrix4x4.Trs(Vector3.Zero, Quaternion.Identity, new Vector3(2, 1, 1));

        BoundingSphere sphere = unit.Transform(stretched);

        Assert.Equal(2f, sphere.Radius, 1e-5f);
    }

    [Fact]
    public void A_transformed_sphere_moves_its_centre()
    {
        BoundingSphere unit = new(Vector3.Zero, 1f);
        Matrix4x4 moved = Matrix4x4.CreateTranslation(10, 0, 0);

        BoundingSphere sphere = unit.Transform(moved);

        Assert.Equal(new Vector3(10, 0, 0), sphere.Center);
    }
}
