using Magic.Contexts.Gpu;
using Magic.Interfaces;

namespace Magic.Contexts.Rendering;

/// <summary>
/// The point and spot lights a view sees, sorted by depth (<see cref="IRendering.CullLights"/>), in the renderer's
/// buffers for the view: the lights, the run of them each depth slice holds, and which of them reach each tile of the
/// view.
/// </summary>
public readonly record struct LightList(GpuBuffer Lights, GpuBuffer Bins, GpuBuffer Tiles);
