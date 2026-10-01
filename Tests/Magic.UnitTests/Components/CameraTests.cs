using Magic.Contexts.Components;
using Magic.Extensions;
using Magic.Mathematics;
using System.Numerics;
using Xunit;

namespace Magic.UnitTests.Components;

public sealed class CameraTests
{
    [Fact]
    public void The_view_matrix_turns_the_cameras_forward_into_plus_z()
    {
        Quaternion rotation = Quaternion.CreateFromYawPitchRoll(0.4f, 0.2f, 0f);
        Matrix4x4 world = Matrix4x4.Trs(new Vector3(5, 6, 7), rotation, Vector3.One);

        Vector3 inView = Vector3.TransformNormal(world.Forward, Camera.ViewMatrix(world));

        Assert.Equal(1f, inView.Z, 1e-5f);
    }

    [Fact]
    public void The_view_matrix_is_camera_relative()
    {
        Matrix4x4 world = Matrix4x4.Trs(new Vector3(5, 6, 7), Quaternion.Identity, Vector3.One);

        Matrix4x4 view = Camera.ViewMatrix(world);

        Assert.Equal(Vector3.Zero, view.Translation);
    }

    [Theory]
    [InlineData(10f, 0f, true)]   // ahead
    [InlineData(0f, 10f, false)]  // to the side
    [InlineData(-10f, 0f, false)] // behind
    public void A_camera_yawed_to_plus_x_sees_only_what_is_ahead(float x, float z, bool expected)
    {
        Camera camera = Camera.Perspective(60f, 0.1f);
        Quaternion towardsPlusX = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);
        Frustum frustum = camera.Frustum(Matrix4x4.Trs(Vector3.Zero, towardsPlusX, Vector3.One), 1f);

        bool intersects = frustum.Intersects(new BoundingSphere(new Vector3(x, 0f, z), 1f));

        Assert.Equal(expected, intersects);
    }
}
