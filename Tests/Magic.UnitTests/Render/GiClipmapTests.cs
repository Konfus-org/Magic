using DeferredRendererGem;
using System.Numerics;
using Xunit;

namespace Magic.UnitTests.Render;

public sealed class GiClipmapTests
{
    [Fact]
    public void The_origin_is_on_the_voxel_grid()
    {
        Vector3 origin = GiClipmap.Origin(new Vector3(10.3f, -2.7f, 5.1f), 64, 0.5f);

        Assert.Equal(origin.X / 0.5f, MathF.Round(origin.X / 0.5f), 1e-4f);
        Assert.Equal(origin.Y / 0.5f, MathF.Round(origin.Y / 0.5f), 1e-4f);
        Assert.Equal(origin.Z / 0.5f, MathF.Round(origin.Z / 0.5f), 1e-4f);
    }

    [Fact]
    public void The_camera_is_within_a_voxel_of_the_levels_centre()
    {
        Vector3 camera = new(10.3f, -2.7f, 5.1f);

        Vector3 origin = GiClipmap.Origin(camera, 64, 1f);
        Vector3 centre = origin + new Vector3(32f);

        Assert.True(Vector3.Distance(camera, centre) < MathF.Sqrt(3f));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, 0)]
    [InlineData(4, 0)]
    [InlineData(1, 1)]
    [InlineData(3, 2)]
    [InlineData(5, 1)]
    public void The_finest_level_updates_every_other_frame_and_the_rest_in_turn(long frame, int level)
    {
        Assert.Equal(level, GiClipmap.UpdateLevel(frame, 3));
    }

    [Fact]
    public void Every_level_updates_within_a_cycle()
    {
        HashSet<int> seen = [];
        for (long frame = 0; frame < 6; frame++)
            seen.Add(GiClipmap.UpdateLevel(frame, 4));

        Assert.Equal(4, seen.Count);
    }

    [Fact]
    public void A_single_level_always_updates()
    {
        Assert.Equal(0, GiClipmap.UpdateLevel(7, 1));
    }

    [Fact]
    public void The_shift_between_origins_is_whole_voxels()
    {
        Vector3 shift = GiClipmap.Shift(new Vector3(0f, 0f, 0f), new Vector3(3f, -2f, 0f), 1f);

        Assert.Equal(new Vector3(3f, -2f, 0f), shift);
    }

    [Theory]
    [InlineData(0, 1f)]
    [InlineData(1, 4f)]
    [InlineData(2, 16f)]
    public void Each_level_is_scale_times_coarser(int level, float voxel)
    {
        Assert.Equal(voxel, GiClipmap.VoxelSize(1f, 4, level));
    }

    [Fact]
    public void Brick_offsets_do_not_overlap()
    {
        (int x0, int y0, int z0) = GiClipmap.BrickOffset(0);
        (int x1, int y1, int z1) = GiClipmap.BrickOffset(1);
        (int x16, int y16, int z16) = GiClipmap.BrickOffset(16);
        (int x256, int y256, int z256) = GiClipmap.BrickOffset(256);

        Assert.Equal((0, 0, 0), (x0, y0, z0));
        Assert.Equal((16, 0, 0), (x1, y1, z1));
        Assert.Equal((0, 16, 0), (x16, y16, z16));
        Assert.Equal((0, 0, 16), (x256, y256, z256));
    }
}
