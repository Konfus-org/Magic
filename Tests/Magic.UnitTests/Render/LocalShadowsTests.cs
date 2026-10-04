using DeferredRendererGem;
using Magic.Extensions;
using System.Numerics;
using Xunit;

namespace Magic.UnitTests.Render;

public sealed class LocalShadowsTests
{
    [Theory]
    [InlineData(0, 1f, 0f, 0f)]
    [InlineData(1, -1f, 0f, 0f)]
    [InlineData(2, 0f, 1f, 0f)]
    [InlineData(3, 0f, -1f, 0f)]
    [InlineData(4, 0f, 0f, 1f)]
    [InlineData(5, 0f, 0f, -1f)]
    public void A_point_lights_face_looks_down_its_axis(int face, float x, float y, float z)
    {
        LightInstance light = new(LightKind.Point, Vector3.One, 1f, 10f, 0f, 0f, true, Matrix4x4.CreateTranslation(new Vector3(3f, 4f, 5f)));

        ShadowFace view = LocalShadows.Point(light, face);
        Vector4 clip = Vector4.Transform(new Vector4(new Vector3(x, y, z) * 5f, 1f), view.ViewProj(light.World.Translation));

        Assert.Equal(0f, clip.X / clip.W, 1e-4f);
        Assert.Equal(0f, clip.Y / clip.W, 1e-4f);
        Assert.InRange(clip.Z / clip.W, 0f, 1f);
    }

    [Fact]
    public void A_point_past_the_lights_range_is_outside_its_face()
    {
        LightInstance light = new(LightKind.Point, Vector3.One, 1f, 10f, 0f, 0f, true, Matrix4x4.Identity);

        ShadowFace view = LocalShadows.Point(light, 0);
        Vector4 clip = Vector4.Transform(new Vector4(20f, 0f, 0f, 1f), view.ViewProj(Vector3.Zero));

        Assert.True(clip.Z / clip.W < 0f);
    }

    [Fact]
    public void A_spot_looks_along_its_forward()
    {
        Matrix4x4 world = Matrix4x4.Trs(new Vector3(1f, 2f, 3f), Quaternion.CreateFromYawPitchRoll(0.5f, 0.4f, 0f), Vector3.One);
        LightInstance light = new(LightKind.Spot, Vector3.One, 1f, 10f, 0.4f, 0.8f, true, world);

        ShadowFace view = LocalShadows.Spot(light);
        Vector4 clip = Vector4.Transform(new Vector4(world.Translation + (Vector3.Normalize(world.Forward) * 4f), 1f), view.ViewProj(Vector3.Zero));

        Assert.Equal(0f, clip.X / clip.W, 1e-4f);
        Assert.Equal(0f, clip.Y / clip.W, 1e-4f);
    }

    [Fact]
    public void A_nearer_light_scores_higher()
    {
        LightInstance near = new(LightKind.Point, Vector3.One, 1f, 5f, 0f, 0f, true, Matrix4x4.CreateTranslation(new Vector3(0f, 0f, 20f)));
        LightInstance far = new(LightKind.Point, Vector3.One, 1f, 5f, 0f, 0f, true, Matrix4x4.CreateTranslation(new Vector3(0f, 0f, 60f)));

        Assert.True(LocalShadows.Score(near, Vector3.Zero, 1.5f) > LocalShadows.Score(far, Vector3.Zero, 1.5f));
    }

    [Fact]
    public void A_light_the_camera_is_inside_fills_the_view()
    {
        LightInstance light = new(LightKind.Point, Vector3.One, 1f, 5f, 0f, 0f, true, Matrix4x4.CreateTranslation(new Vector3(1f, 0f, 0f)));

        Assert.Equal(1f, LocalShadows.Score(light, Vector3.Zero, 1.5f));
    }
}
