using Magic.Contexts;
using Magic.Contexts.Components;
using System.Drawing;
using System.Numerics;
using Xunit;

namespace Magic.UnitTests.Contexts;

public sealed class DebugViewTests
{
    private static readonly Vector3 CameraPosition = new(100, 0, 0);

    /// <summary>
    /// A camera at <see cref="CameraPosition"/> looking along +Z, drawn into an 800 x 600 view whose top left is at pixel (10, 20).
    /// </summary>
    private static readonly DebugView Ahead = new(
        Camera.Perspective(60f, 0.1f).ViewProjection(Matrix4x4.CreateTranslation(CameraPosition), 800f / 600f),
        CameraPosition,
        new RectangleF(10, 20, 800, 600));

    [Fact]
    public void A_position_straight_ahead_is_at_the_centre_of_the_view()
    {
        Ahead.Project(CameraPosition + new Vector3(0, 0, 5), 50f, out Vector2 pixel);

        Assert.Equal(new Vector2(410, 320), pixel);
    }

    [Fact]
    public void A_position_above_the_view_axis_is_above_the_centre()
    {
        Ahead.Project(CameraPosition + new Vector3(0, 1, 5), 50f, out Vector2 pixel);

        Assert.True(pixel.Y < 320);
    }

    [Theory]
    [InlineData(0, 0, 5, true)]    // in front, near
    [InlineData(0, 0, -5, false)]  // behind the camera
    [InlineData(0, 0, 60, false)]  // farther than the distance
    [InlineData(40, 0, 5, false)]  // off to the side of the view
    public void A_position_shows_only_in_front_inside_the_view_and_near(float x, float y, float z, bool shows)
    {
        bool shown = Ahead.Project(CameraPosition + new Vector3(x, y, z), 50f, out _);

        Assert.Equal(shows, shown);
    }
}
