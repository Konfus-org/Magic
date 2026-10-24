using Magic.Contexts.Gpu;
using Magic.Interfaces;

namespace Magic.Contexts.Rendering;

/// <summary>
/// The world as voxels, kept by the renderer from frame to frame and changed only where something moved
/// (<see cref="IRendering.VoxelizeScene"/>): how much each voxel hides light along each axis, every level, and the
/// levels' places, which cones are traced through for shadows and occlusion.
/// </summary>
public readonly record struct VoxelScene(GpuTexture Opacity, GpuBuffer Levels);
