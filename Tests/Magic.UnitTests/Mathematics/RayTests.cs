using Magic.Mathematics;
using System.Numerics;
using Xunit;

namespace Magic.UnitTests.Mathematics;

public sealed class RayTests
{
    private static readonly Aabb BoxAhead = new(new Vector3(-1, -1, 4), new Vector3(1, 1, 6));

    [Fact]
    public void A_ray_hits_a_sphere_ahead_at_its_near_surface()
    {
        Ray ray = new(Vector3.Zero, Axis.Forward);

        float? hit = ray.Intersect(new BoundingSphere(new Vector3(0, 0, 10), 1f));

        Assert.Equal(9f, Assert.NotNull(hit), 1e-5f);
    }

    [Fact]
    public void A_ray_misses_a_sphere_off_its_line()
    {
        Ray ray = new(Vector3.Zero, Axis.Forward);

        float? hit = ray.Intersect(new BoundingSphere(new Vector3(5, 0, 10), 1f));

        Assert.Null(hit);
    }

    [Fact]
    public void A_ray_misses_a_sphere_behind_it()
    {
        Ray ray = new(Vector3.Zero, Axis.Forward);

        float? hit = ray.Intersect(new BoundingSphere(new Vector3(0, 0, -10), 1f));

        Assert.Null(hit);
    }

    [Fact]
    public void A_ray_hits_a_box_ahead_at_its_near_face()
    {
        Ray ray = new(Vector3.Zero, Axis.Forward);

        float? hit = ray.Intersect(BoxAhead);

        Assert.Equal(4f, Assert.NotNull(hit), 1e-5f);
    }

    [Fact]
    public void A_ray_misses_a_box_off_its_line()
    {
        Ray ray = new(Vector3.Zero, Axis.Forward);

        float? hit = ray.Intersect(new Aabb(new Vector3(2, 2, 4), new Vector3(3, 3, 6)));

        Assert.Null(hit);
    }

    [Theory]
    [InlineData(1f, -1f, 0f, 1f, 4f)]   // along an edge: 0 * inf on X and Y
    [InlineData(0f, 0f, 5f, 1f, 0f)]    // starts inside
    [InlineData(0f, 1f, 4f, -1f, 0f)]   // starts on a face, leaving
    public void An_axis_parallel_ray_on_an_edge_or_inside_hits_the_box(float x, float y, float z, float direction, float distance)
    {
        Ray ray = new(new Vector3(x, y, z), Axis.Forward * direction);

        float? hit = ray.Intersect(BoxAhead);

        Assert.Equal(distance, Assert.NotNull(hit), 1e-5f);
    }

    [Theory]
    [InlineData(1.01f, 0f, 0f)] // parallel, just outside
    [InlineData(0f, 0f, 7f)]    // already past it
    public void An_axis_parallel_ray_outside_misses_the_box(float x, float y, float z)
    {
        Ray ray = new(new Vector3(x, y, z), Axis.Forward);

        float? hit = ray.Intersect(BoxAhead);

        Assert.Null(hit);
    }
}
