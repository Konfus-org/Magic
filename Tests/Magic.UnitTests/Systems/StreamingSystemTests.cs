using Magic.Contexts.Components;
using Magic.Mathematics;
using Magic.Systems.Streaming;
using System.Numerics;
using Xunit;

namespace Magic.UnitTests.Systems;

/// <summary>Which chunk cubes a camera wants: pure geometry, no ECS or assets.</summary>
public sealed class StreamingSystemTests
{
    private const float ChunkSize = 64f;

    private static readonly Vector3 Eye = new(10, 5, 10);

    [Fact]
    public void The_cube_under_the_camera_is_the_only_active_one()
    {
        HashSet<(int, int, int)> wanted = [], active = [];

        StreamingSystem.Select(ChunkSize, Eye, LookingForward(), 200f, wanted, active);

        Assert.Equal([(0, 0, 0)], active);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Cubes_straight_ahead_within_the_view_distance_are_wanted(int z)
    {
        HashSet<(int, int, int)> wanted = [];

        StreamingSystem.Select(ChunkSize, Eye, LookingForward(), 200f, wanted);

        Assert.Contains((0, 0, z), wanted);
    }

    [Fact]
    public void A_cube_within_a_chunk_of_the_camera_is_wanted_even_behind_it()
    {
        HashSet<(int, int, int)> wanted = [];

        StreamingSystem.Select(ChunkSize, Eye, LookingForward(), 200f, wanted);

        Assert.Contains((0, 0, -1), wanted); // behind, 10 m away
    }

    [Fact]
    public void A_cube_farther_than_a_chunk_behind_the_camera_is_not_wanted()
    {
        HashSet<(int, int, int)> wanted = [];

        StreamingSystem.Select(ChunkSize, Eye, LookingForward(), 200f, wanted);

        Assert.DoesNotContain((0, 0, -2), wanted);
    }

    [Fact]
    public void A_cube_beyond_the_view_distance_is_not_wanted()
    {
        HashSet<(int, int, int)> wanted = [];

        StreamingSystem.Select(ChunkSize, Eye, LookingForward(), 200f, wanted);

        Assert.DoesNotContain((0, 0, 5), wanted); // 320 m away
    }

    [Fact]
    public void No_view_distance_still_wants_the_cubes_around_the_camera()
    {
        HashSet<(int, int, int)> wanted = [];

        StreamingSystem.Select(ChunkSize, new Vector3(32, 32, 32), LookingForward(), 0f, wanted);

        Assert.Equal(27, wanted.Count); // the camera's cube and its neighbours, each 32 m away
    }

    [Theory]
    [InlineData(-0.5f, 0f, -63.9f, -1, 0, -1)]
    [InlineData(0f, 0f, 0f, 0, 0, 0)]
    [InlineData(63.9f, 64f, -64f, 0, 1, -1)]
    public void Coordinates_floor_towards_negative_infinity(float x, float y, float z, int cx, int cy, int cz)
    {
        (int X, int Y, int Z) coordinate = StreamingSystem.Coordinate(new Vector3(x, y, z), ChunkSize);

        Assert.Equal((cx, cy, cz), coordinate);
    }

    /// <summary>The camera-relative frustum of a 60° camera looking down +Z.</summary>
    private static Frustum LookingForward()
    {
        return Camera.Perspective(60f, 0.05f).Frustum(Matrix4x4.Identity, 2f);
    }
}
