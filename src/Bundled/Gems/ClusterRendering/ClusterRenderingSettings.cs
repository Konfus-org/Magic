using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Magic.Contexts.Assets;
using Magic.Interfaces;

namespace Magic.ClusterRendering;

/// <summary>
/// Which pipeline the renderer runs, and what it computes and allocates: set in the project's
/// <c>Presets/Default.preset</c>, by another preset, or with <c>--set ClusterRendering.renderScale=1</c>.
/// </summary>
[Category("Render")]
internal sealed class ClusterRenderingSettings : ISettings
{
    /// <summary>
    /// The pipeline each frame runs before the world's effects; none for <c>Pipelines/Clusters.pipeline</c>, the
    /// renderer's whole frame, whichever gem or project has it. Changed while running, the frame runs the new one once
    /// it is built.
    /// </summary>
    [Category("Render/Pipeline")]
    public Handle<Pipeline> Pipeline { get; set; }

    /// <summary>
    /// The pipeline run over each target once every view in it is composed, the overlay stage's; none for
    /// <c>Pipelines/Overlay.pipeline</c>, whichever gem or project has it, the debug UI's among them, and nothing when
    /// none does. Changed while running, the frame runs the new one once it is built.
    /// </summary>
    [Category("Render/Pipeline")]
    public Handle<Pipeline> Overlay { get; set; }

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
    /// Whether each view is scaled to its target's size by the frame's temporal upscale, smooth, the post stage run at
    /// the target's size; or else shown as it is drawn, the post stage run at the drawn size and each of its pixels a
    /// crisp block of the target's.
    /// </summary>
    [Category("Render/Display")]
    public bool Upscale { get; set; } = true;

    /// <summary>
    /// Pixels a cluster's error may cover on screen before a finer one is drawn in its place: lower is finer, and more
    /// triangles. The view's height in pixels it is measured in is the size it is drawn at.
    /// </summary>
    [Category("Render/Clusters")]
    [Range(0.25, 16)]
    public float ClusterError { get; set; } = 1;

    /// <summary>
    /// Pieces of up to 32 triangles a view draws at most, which sizes what each view holds of them. Past it, what is
    /// farthest from the player is drawn coarser, never left out.
    /// </summary>
    [Category("Render/Budgets")]
    public int PieceCapacity { get; set; } = 1 << 20;

    /// <summary>
    /// Megabytes of models taken into the pool a frame at most, nearest the player first, so a burst of models
    /// streaming in is spread over frames rather than stalling one.
    /// </summary>
    [Category("Render/Clusters")]
    [Range(0.25, 1024)]
    public float ModelUploadRate { get; set; } = 32;

    /// <summary>
    /// Point and spot lights a view lights pixel by pixel at most, nearest first, a power of two; the farther light
    /// only through the light from everywhere. What a view's light tiles hold a bit for each of.
    /// </summary>
    [Category("Render/Lights")]
    public int LightCapacity { get; set; } = 16384;

    /// <summary>
    /// Megabytes of video memory the pool every shadow is drawn into may take: it is made as large as fits, a power of
    /// two texels a side, which is how many pages of shadow are held at once. Past them, the lights farthest from the
    /// player are shadowed coarser.
    /// </summary>
    [Category("Render/Budgets")]
    [Range(4, 1024)]
    public float ShadowBudget { get; set; } = 256;

    /// <summary>
    /// Levels of the sun's shadow, each twice as wide as the last: how far it reaches, the finest's width doubled again
    /// for each.
    /// </summary>
    [Category("Render/Shadows")]
    [Range(1, 24)]
    public int ShadowLevels { get; set; } = 16;

    /// <summary>
    /// Metres across the sun's finest level of shadow, sharp close up; 64 pages of 120 texels.
    /// </summary>
    [Category("Render/Shadows")]
    public float ShadowFinest { get; set; } = 8;

    /// <summary>
    /// Metres along the sun a page of its shadow holds either side of the camera: as far as what casts into it can be.
    /// </summary>
    [Category("Render/Shadows")]
    public float ShadowDepth { get; set; } = 4000;

    /// <summary>
    /// Degrees the sun, or whichever directional light casts the shadows, turns before its shadow is drawn again for
    /// where it is: a sun crossing the sky redraws its shadow in these steps, finer the more often, each a burst of
    /// pages to draw.
    /// </summary>
    [Category("Render/Shadows")]
    [Range(0.01, 10)]
    public float ShadowTurn { get; set; } = 0.25f;

    /// <summary>
    /// Pages of shadow drawn a frame at most, the sun's coarsest levels first, then lights nearest the player; the rest
    /// wait a frame, drawn coarser till then.
    /// </summary>
    [Category("Render/Shadows")]
    public int ShadowUpdates { get; set; } = 512;

    /// <summary>
    /// The most taps a material's textures are filtered with at a grazing angle: 1 to 16.
    /// </summary>
    [Category("Render/Display")]
    public float Anisotropy { get; set; } = 8;

    /// <summary>
    /// Levels of the world's field and probes for global illumination, each twice the size of the last: 1 to 5.
    /// </summary>
    [Category("Render/Global illumination")]
    public int GiLevels { get; set; } = 5;

    /// <summary>
    /// Metres a voxel of the finest level: its 64 a side and each level after it twice as far reach as far as global
    /// illumination does.
    /// </summary>
    [Category("Render/Global illumination")]
    public float GiVoxel { get; set; } = 0.25f;

    /// <summary>
    /// Rays each probe traces when it is traced: 8 to 256.
    /// </summary>
    [Category("Render/Global illumination")]
    public int GiRays { get; set; } = 64;

    /// <summary>
    /// The finest level's probes are traced every this many frames, a share of them each frame, and each coarser
    /// level's half as often: a power of two.
    /// </summary>
    [Category("Render/Global illumination")]
    public int GiUpdateEvery { get; set; } = 4;
}
