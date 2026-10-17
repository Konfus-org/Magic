using System.Numerics;
using Magic.Contexts.Components;
using Magic.Contexts.Utils;

namespace Magic.Tests.Contexts.Components;

public sealed class TransformTests
{
    private const int Places = 5;

    [Fact]
    public void RelativeTo_moves_the_position_by_the_origin()
    {
        Transform transform = new() { Position = new Vector3D(10, 20, 30) };

        Matrix4x4 matrix = transform.RelativeTo(new Vector3D(4, 5, 6));

        Assert.Equal(new Vector3(6, 15, 24), matrix.Translation);
    }

    [Fact]
    public void RelativeTo_scales_then_turns_then_moves_a_point()
    {
        // The scale is along X only, so scaling before the turn and after it give different points.
        Transform transform = new()
        {
            Position = new Vector3D(0, 0, 10),
            Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2),
            Scale = new Vector3(2, 1, 1),
        };

        Vector3 placed = Vector3.Transform(Vector3.UnitZ, transform.RelativeTo(default));

        Assert.Equal(1, placed.X, Places);
        Assert.Equal(0, placed.Y, Places);
        Assert.Equal(10, placed.Z, Places);
    }

    [Fact]
    public void Under_moves_the_child_by_the_parents_scaled_then_turned_offset()
    {
        // The scale is along X only, so scaling before the turn and after it give different points.
        Transform parent = new()
        {
            Position = new Vector3D(0, 0, 10),
            Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2),
            Scale = new Vector3(2, 1, 1),
        };
        Transform child = new() { Position = new Vector3D(0, 0, 1) };

        Transform placed = child.Under(parent);

        Assert.Equal(1, placed.Position.X, Places);
        Assert.Equal(0, placed.Position.Y, Places);
        Assert.Equal(10, placed.Position.Z, Places);
    }

    [Fact]
    public void Under_turns_the_child_by_its_own_rotation_then_the_parents()
    {
        // Turns about different axes, so the order they are applied in shows in where forward ends up.
        Transform parent = new() { Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2) };
        Transform child = new() { Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2) };

        Vector3 forward = Vector3.Transform(Vector3.UnitZ, child.Under(parent).Rotation);

        Assert.Equal(0, forward.X, Places);
        Assert.Equal(-1, forward.Y, Places);
        Assert.Equal(0, forward.Z, Places);
    }

    [Fact]
    public void Under_multiplies_the_scales()
    {
        Transform parent = new() { Scale = new Vector3(2, 2, 2) };
        Transform child = new() { Scale = new Vector3(3, 1, 1) };

        Transform placed = child.Under(parent);

        Assert.Equal(new Vector3(6, 2, 2), placed.Scale);
    }
}
