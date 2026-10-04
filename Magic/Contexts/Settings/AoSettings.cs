namespace Magic.Contexts.Settings;

/// <summary>
/// Screen-space ambient occlusion (GTAO) with bent normals: the contact shadows the ambient light gets per pixel.
/// All of it is read every frame; a change in resolution remakes the occlusion textures the next frame.
/// </summary>
public sealed class AoSettings
{
    private float _radius = 1f;
    private float _maxRadiusPixels = 64f;
    private int _slices = 3;
    private int _steps = 4;
    private float _strength = 1f;
    private float _thickness = 0.5f;

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Compute the occlusion at half the view's resolution and upsample it: a quarter of the cost, a little softer.
    /// </summary>
    public bool HalfResolution { get; set; } = true;

    /// <summary>
    /// Metres around a pixel that can occlude it.
    /// </summary>
    public float Radius { get => _radius; set => _radius = Math.Clamp(value, 0.05f, 10f); }

    /// <summary>
    /// The most pixels the radius projects to, so a near surface never searches the whole screen.
    /// </summary>
    public float MaxRadiusPixels { get => _maxRadiusPixels; set => _maxRadiusPixels = Math.Clamp(value, 4f, 256f); }

    /// <summary>
    /// Directions searched around each pixel; more is smoother and costs as much more.
    /// </summary>
    public int Slices { get => _slices; set => _slices = Math.Clamp(value, 1, 8); }

    /// <summary>
    /// Samples along each direction, each way.
    /// </summary>
    public int Steps { get => _steps; set => _steps = Math.Clamp(value, 1, 16); }

    /// <summary>
    /// How much of the occlusion is applied: 1 all of it, 0 none.
    /// </summary>
    public float Strength { get => _strength; set => _strength = Math.Clamp(value, 0f, 1f); }

    /// <summary>
    /// Let a bright surface fill in its own creases, so a white room's corners are not grey.
    /// </summary>
    public bool MultiBounce { get; set; } = true;

    /// <summary>
    /// The share of the radius over which an occluder counts in full before it fades with distance; lower treats thin things as thin.
    /// </summary>
    public float Thickness { get => _thickness; set => _thickness = Math.Clamp(value, 0f, 0.95f); }
}
