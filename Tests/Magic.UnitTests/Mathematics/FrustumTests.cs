using Magic.Extensions;
using Magic.Mathematics;
using System.Numerics;
using Xunit;

namespace Magic.UnitTests.Mathematics;

public sealed class FrustumTests
{
    [Theory]
    [InlineData(0f, 0f, 10f, 1f, true)]      // straight ahead
    [InlineData(0f, 0f, -10f, 1f, false)]    // behind
    [InlineData(50f, 0f, 10f, 1f, false)]    // far right of a 90° cone
    [InlineData(10.5f, 0f, 10f, 1f, true)]   // straddles the right plane
    [InlineData(0f, 0f, 0f, 5f, true)]       // the camera is inside it
    public void A_sphere_intersects_when_any_of_it_is_inside(float x, float y, float z, float radius, bool expected)
    {
        BoundingSphere sphere = new(new Vector3(x, y, z), radius);

        bool intersects = Frustum90().Intersects(sphere);

        Assert.Equal(expected, intersects);
    }

    [Theory]
    [InlineData(9f, 12f, true)]   // straddles the right plane
    [InlineData(20f, 22f, false)] // wholly outside it
    public void A_box_intersects_when_any_of_it_is_inside(float minX, float maxX, bool expected)
    {
        Aabb box = new(new Vector3(minX, -1, 9), new Vector3(maxX, 1, 11));

        bool intersects = Frustum90().Intersects(box);

        Assert.Equal(expected, intersects);
    }

    [Theory]
    [InlineData(1f, true)]
    [InlineData(0.05f, false)] // before the near plane
    public void A_point_is_contained_past_the_near_plane(float z, bool expected)
    {
        bool contains = Frustum90().Contains(new Vector3(0, 0, z));

        Assert.Equal(expected, contains);
    }

    [Theory]
    [InlineData(0, 1f, 0f, 0f)]  // left
    [InlineData(1, -1f, 0f, 0f)] // right
    [InlineData(2, 0f, 1f, 0f)]  // bottom
    [InlineData(3, 0f, -1f, 0f)] // top
    [InlineData(4, 0f, 0f, 1f)]  // near
    public void The_planes_are_left_right_bottom_top_near_facing_inwards(int index, float x, float y, float z)
    {
        Plane plane = Frustum90().Planes[index];

        Assert.True(Vector3.Dot(plane.Normal, new Vector3(x, y, z)) > 0f);
    }

    [Fact]
    public void An_infinite_projection_has_a_far_plane_nothing_is_behind()
    {
        Plane far = Frustum90().Planes[5];

        Assert.Equal(Vector3.Zero, far.Normal);
    }

    /// <summary>A 90° camera at the origin looking down +Z, with an infinite far plane.</summary>
    private static Frustum Frustum90()
    {
        return Frustum.FromViewProjection(Matrix4x4.PerspectiveReverseZ(float.DegreesToRadians(90f), 1f, 0.1f));
    }
}
