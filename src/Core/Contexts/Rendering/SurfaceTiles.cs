using Magic.Contexts.Gpu;
using Magic.Interfaces;

namespace Magic.Contexts.Rendering;

/// <summary>
/// A view's tiles sorted by the surfaces their pixels show (<see cref="IRendering.Classify"/>), in the renderer's
/// buffers for the view: the pieces the pixels name, each surface's tiles, and the draw of each surface over its tiles.
/// </summary>
public readonly record struct SurfaceTiles(GpuBuffer Pieces, GpuBuffer Tiles, GpuBuffer Draws);
