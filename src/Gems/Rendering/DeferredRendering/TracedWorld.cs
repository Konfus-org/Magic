using System.Numerics;
using Magic.DeferredRendering.View;
using Magic.Graphics;

namespace Magic.DeferredRendering;

/// <summary>
/// The world as rays see it this frame, on a GPU that traces them.
/// </summary>
/// <remarks>
/// It holds the acceleration structure over the instances, each material row as a ray hit reads it, and the box over
/// the world boxes of the instances it holds, min over max when it holds none. Its ids are in the frame's buffer as
/// <see cref="GpuTracedWorld"/>.
/// </remarks>
public readonly record struct TracedWorld(
    GpuAcceleration Structure,
    GpuBuffer HitMaterials,
    (Vector3 Min, Vector3 Max) Box
);

/// <summary>
/// What a gem that traces rays through the world takes: the view and the world as rays see it.
/// </summary>
public readonly record struct TracedInput(GpuView View, TracedWorld Traced);
