using Magic.Contexts.Gpu;
using Magic.Interfaces;

namespace Magic.Contexts.Rendering;

/// <summary>
/// What a culling of a view found in sight (<see cref="IRendering.Cull"/>), in the renderer's buffers for the view:
/// its phase, the pieces it placed, of up to 32 triangles each, and the draws that draw them, from row
/// <paramref name="FirstDraw"/> of <paramref name="Draws"/>.
/// </summary>
public readonly record struct VisibleWorld(CullPhase Phase, GpuBuffer Pieces, GpuBuffer Draws, uint FirstDraw);
