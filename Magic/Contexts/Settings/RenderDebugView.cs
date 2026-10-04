namespace Magic.Contexts.Settings;

/// <summary>
/// What the lighting shows in place of the lit scene, for looking at one of its inputs: <see cref="None"/> is the scene.
/// Twin: the <c>DebugView*</c> constants in <c>Include/Shade.hlsli</c>.
/// </summary>
public enum RenderDebugView : byte
{
    None,

    /// <summary>
    /// The sun's cascades, one colour each (red, green, blue, yellow), darkened where the sun is shadowed.
    /// </summary>
    Shadows,

    /// <summary>
    /// The ambient occlusion alone.
    /// </summary>
    Ao,

    /// <summary>
    /// The bent normals the ambient light is gathered along.
    /// </summary>
    BentNormal,

    /// <summary>
    /// The light the GI volume gives a surface.
    /// </summary>
    GiRadiance,

    /// <summary>
    /// How much of the sky a surface sees, from the GI volume.
    /// </summary>
    SkyVisibility,

    /// <summary>
    /// What the GI volume holds at the surface: the voxels' albedo, and how full they are.
    /// </summary>
    VoxelAlbedo,

    VoxelCoverage,
}
