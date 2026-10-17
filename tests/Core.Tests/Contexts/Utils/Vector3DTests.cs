using System.Numerics;
using Magic.Contexts.Utils;

namespace Magic.Tests.Contexts.Utils;

public sealed class Vector3DTests
{
    private const int Places = 6;

    [Fact]
    public void Add_sums_each_axis()
    {
        Vector3D left = new(1, 2, 3);
        Vector3D right = new(10, 20, 30);

        Vector3D sum = left + right;

        Assert.Equal(new Vector3D(11, 22, 33), sum);
    }

    [Fact]
    public void Subtract_takes_each_axis()
    {
        Vector3D left = new(10, 20, 30);
        Vector3D right = new(1, 2, 3);

        Vector3D difference = left - right;

        Assert.Equal(new Vector3D(9, 18, 27), difference);
    }

    [Fact]
    public void Multiply_scales_each_axis()
    {
        Vector3D vector = new(1, 2, 3);

        Vector3D scaled = vector * 2;

        Assert.Equal(new Vector3D(2, 4, 6), scaled);
    }

    [Fact]
    public void Multiply_by_a_vector_scales_each_axis_by_its_own()
    {
        Vector3D vector = new(1, 2, 3);

        Vector3D scaled = vector * new Vector3(2, 3, 4);

        Assert.Equal(new Vector3D(2, 6, 12), scaled);
    }

    [Fact]
    public void Dot_sums_the_products_of_the_axes()
    {
        Vector3D left = new(1, 2, 3);
        Vector3D right = new(4, 5, 6);

        double dot = Vector3D.Dot(left, right);

        Assert.Equal(32, dot);
    }

    [Fact]
    public void Cross_of_x_and_y_is_z()
    {
        Vector3D x = new(1, 0, 0);
        Vector3D y = new(0, 1, 0);

        Vector3D cross = Vector3D.Cross(x, y);

        Assert.Equal(new Vector3D(0, 0, 1), cross);
    }

    [Fact]
    public void Distance_is_the_length_between_two_points()
    {
        Vector3D from = new(1, 2, 3);
        Vector3D to = new(4, 6, 3);

        double distance = Vector3D.Distance(from, to);

        Assert.Equal(5, distance);
    }

    [Fact]
    public void Min_takes_the_smaller_of_each_axis()
    {
        Vector3D left = new(1, 5, 3);
        Vector3D right = new(2, 4, 6);

        Vector3D min = Vector3D.Min(left, right);

        Assert.Equal(new Vector3D(1, 4, 3), min);
    }

    [Fact]
    public void Max_takes_the_larger_of_each_axis()
    {
        Vector3D left = new(1, 5, 3);
        Vector3D right = new(2, 4, 6);

        Vector3D max = Vector3D.Max(left, right);

        Assert.Equal(new Vector3D(2, 5, 6), max);
    }

    [Fact]
    public void Length_is_the_straight_line_from_the_origin()
    {
        Vector3D vector = new(3, 4, 0);

        double length = vector.Length;

        Assert.Equal(5, length);
    }

    [Fact]
    public void Normalized_has_length_one()
    {
        Vector3D vector = new(3, 4, 0);

        Vector3D normalized = vector.Normalized();

        Assert.Equal(1, normalized.Length, Places);
    }

    [Fact]
    public void Normalized_keeps_the_direction()
    {
        Vector3D vector = new(3, 4, 0);

        Vector3D normalized = vector.Normalized();

        Assert.Equal(0.6, normalized.X, Places);
        Assert.Equal(0.8, normalized.Y, Places);
        Assert.Equal(0, normalized.Z, Places);
    }

    [Fact]
    public void A_zero_vector_normalized_stays_zero()
    {
        Vector3D zero = new(0, 0, 0);

        Vector3D normalized = zero.Normalized();

        Assert.Equal(zero, normalized);
    }

    [Fact]
    public void Transform_turns_the_vector_about_the_rotations_axis()
    {
        Vector3D forward = new(0, 0, 1);
        Quaternion quarterTurnAboutY = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);

        Vector3D turned = Vector3D.Transform(forward, quarterTurnAboutY);

        Assert.Equal(1, turned.X, Places);
        Assert.Equal(0, turned.Y, Places);
        Assert.Equal(0, turned.Z, Places);
    }

    [Fact]
    public void ToVector3_keeps_each_axis()
    {
        Vector3D vector = new(1.5, -2, 3);

        Vector3 floats = vector.ToVector3();

        Assert.Equal(new Vector3(1.5f, -2, 3), floats);
    }

    [Fact]
    public void A_vector3_converts_to_it_axis_by_axis()
    {
        Vector3 floats = new(1, 2, 3);

        Vector3D vector = floats;

        Assert.Equal(new Vector3D(1, 2, 3), vector);
    }
}
