using DeferredRendererGem;
using Magic.Extensions;
using System.Numerics;
using Xunit;

namespace Magic.UnitTests.Render;

public sealed class CascadesTests
{
    private const float Fov = 1.0f;
    private const float Aspect = 16f / 9f;

    [Fact]
    public void The_last_split_is_the_shadow_distance()
    {
        Span<float> fars = stackalloc float[4];

        Cascades.Split(0.05f, 300f, 0.7f, fars);

        Assert.Equal(300f, fars[3]);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    [InlineData(1f)]
    public void Splits_increase_from_near_to_far(float lambda)
    {
        Span<float> fars = stackalloc float[4];

        Cascades.Split(0.1f, 200f, lambda, fars);

        for (int i = 1; i < fars.Length; i++)
            Assert.True(fars[i] > fars[i - 1]);
    }

    [Fact]
    public void Uniform_splits_are_evenly_spaced()
    {
        Span<float> fars = stackalloc float[4];

        Cascades.Split(0f, 100f, 0f, fars);

        Assert.Equal(25f, fars[0], 1e-4f);
        Assert.Equal(50f, fars[1], 1e-4f);
        Assert.Equal(75f, fars[2], 1e-4f);
    }

    [Fact]
    public void Logarithmic_splits_have_a_constant_ratio()
    {
        Span<float> fars = stackalloc float[3];

        Cascades.Split(1f, 1000f, 1f, fars);

        Assert.Equal(10f, fars[0], 1e-3f);
        Assert.Equal(100f, fars[1], 1e-2f);
    }

    [Fact]
    public void Every_corner_of_the_slice_is_inside_the_fitted_sphere()
    {
        Matrix4x4 camera = Matrix4x4.Trs(new Vector3(10f, 30f, -5f), Quaternion.CreateFromYawPitchRoll(0.7f, -0.3f, 0f), Vector3.One);
        Matrix4x4 light = Cascades.LightRotation(new Vector3(0.3f, -0.8f, 0.5f));
        Span<Vector3> corners = stackalloc Vector3[8];
        Cascades.SliceCorners(camera, Fov, Aspect, 5f, 40f, corners);

        Cascade cascade = Cascades.Fit(camera, Fov, Aspect, 5f, 40f, light, 1024, 100f);

        foreach (Vector3 corner in corners)
            Assert.True(Vector3.Distance(corner, cascade.Center) <= cascade.Radius + (2f * cascade.TexelWorld));
    }

    [Fact]
    public void The_view_projection_maps_the_slice_corners_into_clip_space()
    {
        Matrix4x4 camera = Matrix4x4.Trs(new Vector3(0f, 2f, 0f), Quaternion.Identity, Vector3.One);
        Matrix4x4 light = Cascades.LightRotation(new Vector3(0f, -1f, 0.2f));
        Span<Vector3> corners = stackalloc Vector3[8];
        Cascades.SliceCorners(camera, Fov, Aspect, 1f, 20f, corners);
        Cascade cascade = Cascades.Fit(camera, Fov, Aspect, 1f, 20f, light, 1024, 50f);

        Matrix4x4 viewProj = cascade.ViewProj(camera.Translation);

        foreach (Vector3 corner in corners)
        {
            Vector4 clip = Vector4.Transform(new Vector4(corner - camera.Translation, 1f), viewProj);
            Assert.InRange(clip.X, -1.01f, 1.01f);
            Assert.InRange(clip.Y, -1.01f, 1.01f);
            Assert.InRange(clip.Z, 0f, 1f);
        }
    }

    [Fact]
    public void A_caster_towards_the_light_within_the_caster_range_is_inside_the_volume()
    {
        Matrix4x4 camera = Matrix4x4.Identity;
        Vector3 direction = Vector3.Normalize(new Vector3(0f, -1f, 0.2f));
        Cascade cascade = Cascades.Fit(camera, Fov, Aspect, 1f, 20f, Cascades.LightRotation(direction), 1024, 100f);

        Vector3 caster = cascade.Center - (direction * (cascade.Radius + 50f));
        Vector4 clip = Vector4.Transform(new Vector4(caster, 1f), cascade.ViewProj(Vector3.Zero));

        Assert.InRange(clip.Z, 0f, 1f);
    }

    [Fact]
    public void Rotating_the_camera_keeps_the_radius()
    {
        Matrix4x4 light = Cascades.LightRotation(new Vector3(0.3f, -0.8f, 0.5f));
        Matrix4x4 facingZ = Matrix4x4.Identity;
        Matrix4x4 facingX = Matrix4x4.Trs(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1.3f), Vector3.One);

        Cascade first = Cascades.Fit(facingZ, Fov, Aspect, 2f, 30f, light, 1024, 100f);
        Cascade second = Cascades.Fit(facingX, Fov, Aspect, 2f, 30f, light, 1024, 100f);

        Assert.Equal(first.Radius, second.Radius);
    }

    [Fact]
    public void Moving_the_camera_less_than_a_texel_keeps_the_centre_on_the_texel_grid()
    {
        Matrix4x4 light = Cascades.LightRotation(new Vector3(0.3f, -0.8f, 0.5f));
        Cascade first = Cascades.Fit(Matrix4x4.Identity, Fov, Aspect, 2f, 30f, light, 1024, 100f);
        Matrix4x4 moved = Matrix4x4.CreateTranslation(new Vector3(0.3f * first.TexelWorld, 0f, 0.2f * first.TexelWorld));

        Cascade second = Cascades.Fit(moved, Fov, Aspect, 2f, 30f, light, 1024, 100f);

        Vector3 delta = Vector3.Transform(second.Center, light) - Vector3.Transform(first.Center, light);
        Assert.Equal(MathF.Round(delta.X / first.TexelWorld), delta.X / first.TexelWorld, 1e-3f);
        Assert.Equal(MathF.Round(delta.Y / first.TexelWorld), delta.Y / first.TexelWorld, 1e-3f);
    }

    [Fact]
    public void A_caster_between_the_slice_and_the_sun_is_inside_the_receiver_volume()
    {
        Matrix4x4 camera = Matrix4x4.Identity;
        Vector3 sun = Vector3.Normalize(new Vector3(0.3f, -0.8f, 0.5f));
        Span<Vector4> planes = stackalloc Vector4[Cascades.MaxReceiverPlanes];

        int count = Cascades.ReceiverPlanes(camera, Fov, Aspect, 5f, 40f, sun, Vector3.Zero, planes);
        Vector3 caster = new Vector3(0f, 0f, 20f) - (sun * 30f);

        Assert.True(count > 0);
        for (int i = 0; i < count; i++)
            Assert.True(Vector3.Dot(new Vector3(planes[i].X, planes[i].Y, planes[i].Z), caster) + planes[i].W >= 0f);
    }

    [Fact]
    public void A_caster_beside_the_slice_is_outside_the_receiver_volume()
    {
        Matrix4x4 camera = Matrix4x4.Identity;
        Vector3 sun = Vector3.Normalize(new Vector3(0f, -1f, 0.1f));
        Span<Vector4> planes = stackalloc Vector4[Cascades.MaxReceiverPlanes];

        int count = Cascades.ReceiverPlanes(camera, Fov, Aspect, 5f, 40f, sun, Vector3.Zero, planes);
        Vector3 caster = new(200f, 0f, 20f);

        bool inside = true;
        for (int i = 0; i < count; i++)
            inside &= Vector3.Dot(new Vector3(planes[i].X, planes[i].Y, planes[i].Z), caster) + planes[i].W >= 0f;
        Assert.False(inside);
    }

    [Fact]
    public void The_light_rotation_turns_the_light_direction_into_plus_z()
    {
        Vector3 direction = Vector3.Normalize(new Vector3(0.3f, -0.8f, 0.5f));

        Vector3 inLight = Vector3.TransformNormal(direction, Cascades.LightRotation(direction));

        Assert.Equal(1f, inLight.Z, 1e-5f);
    }

    [Fact]
    public void Tiles_of_one_row_do_not_overlap()
    {
        System.Drawing.Rectangle first = Cascades.Tile(0, 1, 512);
        System.Drawing.Rectangle second = Cascades.Tile(1, 1, 512);

        Assert.False(first.IntersectsWith(second));
        Assert.Equal(512, first.Y);
    }
}
