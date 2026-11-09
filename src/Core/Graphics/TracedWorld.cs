namespace Magic.Graphics;

/// <summary>
/// The world as rays see it this frame, on a GPU that traces them: the acceleration structure over the instances and
/// each material row as a ray hit reads it. Its ids are in the frame's buffer as <see cref="GpuTracedWorld"/>.
/// </summary>
public readonly record struct TracedWorld(GpuAcceleration Structure, GpuBuffer HitMaterials);

/// <summary>
/// What a gem that traces rays through the world takes: the view and the world as rays see it.
/// </summary>
public readonly record struct TracedInput(GpuView View, TracedWorld Traced);
