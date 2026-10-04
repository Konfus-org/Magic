using System.Numerics;

namespace Magic.Contexts.Settings;

/// <summary>
/// The fake global illumination: a clipmap of voxel volumes around the camera (each level four times the voxel size of
/// the one before) holding what the scene is made of, a distance field of it, how much sky each voxel sees, and the
/// light bounced once through it. A change to the levels, resolution or scale makes the volumes again next frame; the
/// rest is read every frame.
/// </summary>
public sealed class GiSettings
{
    private int _levels = 3;
    private int _resolution = 48;
    private float _voxelSize = 1f;
    private int _levelScale = 4;
    private float _intensity = 1f;
    private float _interiorStrength = 1f;
    private float _propagationDamping = 0.9f;
    private float _fadeVoxels = 4f;
    private int _bricksPerFrame = 8;

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How many volumes nest around the camera, 1 to 4: each covers <see cref="LevelScale"/> times the distance of the one before.
    /// </summary>
    public int Levels { get => _levels; set => _levels = Math.Clamp(value, 1, 4); }

    /// <summary>
    /// Voxels a side of each level, a multiple of 8 from 16 to 128; the memory grows with its cube.
    /// </summary>
    public int Resolution { get => _resolution; set => _resolution = Math.Clamp(value / 8 * 8, 16, 128); }

    /// <summary>
    /// Metres a voxel of the finest level is.
    /// </summary>
    public float VoxelSize { get => _voxelSize; set => _voxelSize = Math.Clamp(value, 0.1f, 16f); }

    /// <summary>
    /// How many times coarser each level is than the one before, 2 to 8.
    /// </summary>
    public int LevelScale { get => _levelScale; set => _levelScale = Math.Clamp(value, 2, 8); }

    /// <summary>
    /// Multiplies the bounced light.
    /// </summary>
    public float Intensity { get => _intensity; set => _intensity = Math.Clamp(value, 0f, 8f); }

    /// <summary>
    /// The linear colour an interior is softly lit with where the sky cannot reach, times <see cref="InteriorStrength"/>.
    /// </summary>
    public Vector3 InteriorTint { get; set; } = new(0.03f, 0.035f, 0.05f);

    public float InteriorStrength { get => _interiorStrength; set => _interiorStrength = Math.Clamp(value, 0f, 8f); }

    /// <summary>
    /// How much of the light a voxel gets from its neighbours it passes on: under 1, so the bounce settles.
    /// </summary>
    public float PropagationDamping { get => _propagationDamping; set => _propagationDamping = Math.Clamp(value, 0f, 0.98f); }

    /// <summary>
    /// Voxels from a level's edge over which it blends into the next coarser level.
    /// </summary>
    public float FadeVoxels { get => _fadeVoxels; set => _fadeVoxels = Math.Clamp(value, 1f, 16f); }

    /// <summary>
    /// How many meshes have their occupancy brick built per frame.
    /// </summary>
    public int BricksPerFrame { get => _bricksPerFrame; set => _bricksPerFrame = Math.Clamp(value, 1, 64); }
}
