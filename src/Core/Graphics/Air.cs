namespace Magic.Graphics;

/// <summary>
/// The air between the camera and what it sees this frame: a volume over the view of the light the air scatters towards
/// the camera and how much of what is behind gets through, and, where the sky has clouds, a picture of them over the
/// sky. Their ids are in the frame's buffer as <see cref="GpuAir"/>.
/// </summary>
public readonly record struct Air(GpuTexture Volume, GpuTexture? Clouds);
