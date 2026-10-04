using System.Numerics;

namespace DeferredRendererGem;

/// <summary>
/// The GI clipmap as maths: where each level sits around the camera, which level a frame updates, and how a level's
/// stored light is found again after its origin moved. No GPU and no state.
/// </summary>
internal static class GiClipmap
{
    public static float VoxelSize(float baseSize, int scale, int level)
    {
        return baseSize * MathF.Pow(scale, level);
    }

    /// <summary>
    /// A level's min corner: the camera at its centre, snapped to its own voxel grid so the volume only ever moves by
    /// whole voxels and what it holds lines up with what it held.
    /// </summary>
    public static Vector3 Origin(Vector3 camera, int resolution, float voxel)
    {
        Vector3 corner = camera - new Vector3(resolution * voxel * 0.5f);
        return new Vector3(MathF.Floor(corner.X / voxel), MathF.Floor(corner.Y / voxel), MathF.Floor(corner.Z / voxel)) * voxel;
    }

    /// <summary>
    /// The level a frame rebuilds: the finest every other frame, the coarser ones in turn between.
    /// </summary>
    public static int UpdateLevel(long frame, int levels)
    {
        if (levels <= 1 || (frame & 1) == 0)
            return 0;

        return (int)((frame / 2) % (levels - 1)) + 1;
    }

    /// <summary>
    /// How many voxels a level's origin moved, as whole voxels: where a voxel of the new volume sits in the old one.
    /// </summary>
    public static Vector3 Shift(Vector3 oldOrigin, Vector3 newOrigin, float voxel)
    {
        Vector3 moved = (newOrigin - oldOrigin) / voxel;
        return new Vector3(MathF.Round(moved.X), MathF.Round(moved.Y), MathF.Round(moved.Z));
    }

    /// <summary>
    /// The texel offset of a brick in the brick atlas (<see cref="GiVolumes.BrickSize"/> a side, <see cref="GiVolumes.BricksAcross"/>
    /// across and down).
    /// </summary>
    public static (int X, int Y, int Z) BrickOffset(uint brick)
    {
        int across = GiVolumes.BricksAcross;
        return ((int)(brick % across) * GiVolumes.BrickSize, (int)(brick / across % across) * GiVolumes.BrickSize, (int)(brick / (across * across)) * GiVolumes.BrickSize);
    }
}
