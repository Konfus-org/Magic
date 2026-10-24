using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Magic.Contexts.Assets;
using Magic.Interfaces;

namespace Magic.VoxelRendering;

/// <summary>
/// Which pipeline script the renderer runs, and what it computes and allocates: set in the project's
/// <c>Presets/Default.preset</c>, by another preset, or with <c>--set VoxelRendering.renderScale=1</c>.
/// </summary>
[Category("Render")]
internal sealed class VoxelRenderingSettings : ISettings
{
    /// <summary>
    /// The pipeline script that lays the frame out; none for <c>Scripts/DefaultPipeline.cs</c>, the renderer's own,
    /// whichever gem or project has it. Changed while running, the frame is drawn by the new one once its shaders are
    /// compiled.
    /// </summary>
    [Category("Render/Pipeline")]
    public Handle<Script> Pipeline { get; set; }

    /// <summary>
    /// The script drawn over the window once every view in it is shown; none for <c>Scripts/DebugUi.cs</c>, the debug
    /// UI's, whichever gem or project has it, and nothing when none does.
    /// </summary>
    [Category("Render/Pipeline")]
    public Handle<Script> Overlay { get; set; }

    /// <summary>
    /// The share of its target's size each view is drawn at, across and down, then scaled to fill it: 1 for the
    /// target's own, less to draw fewer pixels and upscale them, more to draw more and scale them down. A share rather
    /// than pixels, so it is right on any display and any render texture. A width or height named below takes its
    /// place.
    /// </summary>
    [Category("Render/Display")]
    [Range(0.1, 2)]
    public float RenderScale { get; set; } = 2f / 3;

    /// <summary>
    /// Pixels across each view is drawn at in place of its render scale, then scaled to fill its target, for a look
    /// that holds whatever the display; 0 for the scale's, or, with a height, the width that keeps the target's
    /// proportions.
    /// </summary>
    [Category("Render/Display")]
    public int RenderWidth { get; set; }

    /// <summary>
    /// Pixels down each view is drawn at in place of its render scale, then scaled to fill its target: 180 with the
    /// upscale off for a crunchy, pixelated look; 0 for the scale's, or, with a width, the height that keeps the
    /// target's proportions.
    /// </summary>
    [Category("Render/Display")]
    public int RenderHeight { get; set; }

    /// <summary>
    /// Whether each view is scaled smoothly to its target's size, or shown as it is drawn, each of its pixels a crisp
    /// block of the target's.
    /// </summary>
    [Category("Render/Display")]
    public bool IsUpscaled { get; set; } = true;

    /// <summary>
    /// The most taps a material's textures are filtered with at a grazing angle: 1 to 16.
    /// </summary>
    [Category("Render/Display")]
    public float Anisotropy { get; set; } = 8;

    /// <summary>
    /// Pixels a cluster's error may cover on screen before a finer one is drawn in its place: lower is finer, and more
    /// triangles. The view's height in pixels it is measured in is the size it is drawn at.
    /// </summary>
    [Category("Render/Clusters")]
    [Range(0.25, 16)]
    public float ClusterError { get; set; } = 1;

    /// <summary>
    /// Megabytes of models taken into the pool a frame at most, nearest the player first, so a burst of models
    /// streaming in is spread over frames rather than stalling one.
    /// </summary>
    [Category("Render/Clusters")]
    [Range(0.25, 1024)]
    public float ModelUploadRate { get; set; } = 32;

    /// <summary>
    /// Pieces of up to 32 triangles a view draws at most, which sizes what each view holds of them. Past it, what is
    /// farthest from the player is drawn coarser, never left out.
    /// </summary>
    [Category("Render/Budgets")]
    public int PieceCapacity { get; set; } = 1 << 20;

    /// <summary>
    /// Metres a voxel of the finest level is a side. The voxels, which shadows and occlusion are traced through, are
    /// levels of 128 a side about the player's camera, each level's twice the last's: smaller is sharper near the camera
    /// and reaches less far.
    /// </summary>
    [Category("Render/Voxels")]
    [Range(0.01, 4)]
    public float VoxelSize { get; set; } = 0.0625f;

    /// <summary>
    /// Levels of voxels, 1 to 8: each reaches twice as far as the one before.
    /// </summary>
    [Category("Render/Voxels")]
    [Range(1, 8)]
    public int VoxelLevels { get; set; } = 8;

    /// <summary>
    /// Point and spot lights a view lights by at most, up to 4096, which sizes what each view holds of them. Past it,
    /// the lights found after are left out.
    /// </summary>
    [Category("Render/Budgets")]
    [Range(32, 4096)]
    public int LightCapacity { get; set; } = 4096;
}
