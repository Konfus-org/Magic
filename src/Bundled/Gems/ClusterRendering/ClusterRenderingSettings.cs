using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Magic.Contexts.Assets;

namespace Magic.ClusterRendering;

/// <summary>
/// Which pipeline the renderer runs, and what it computes and allocates: set in the project's
/// <c>Presets/Default.preset</c>, by another preset, or with <c>--set ClusterRendering.renderScale=0.67</c>.
/// </summary>
internal sealed class ClusterRenderingSettings
{
    /// <summary>
    /// The pipeline each frame runs before the world's effects; none for the default, <c>Pipelines/Default.pipeline</c>.
    /// </summary>
    [Browsable(false)]
    public Handle<Pipeline> Pipeline { get; set; }

    /// <summary>
    /// Of its target's size each view is drawn at, then scaled up to fill it: below 1 for a slower GPU, 0.1 to 2.
    /// </summary>
    [Range(0.1, 2)]
    public float RenderScale { get; set; } = 1;

    /// <summary>
    /// Pixels a cluster's error may cover on screen before a finer one is drawn in its place: lower is finer, and more
    /// triangles. The view's height in pixels it is measured in is the size it is drawn at.
    /// </summary>
    [Range(0.25, 16)]
    public float ClusterError { get; set; } = 1;

    /// <summary>
    /// Pieces of up to 32 triangles a view draws at most. Past it, what is farthest from the player is drawn coarser,
    /// never left out.
    /// </summary>
    public int PieceCapacity { get; set; } = 1 << 20;

    /// <summary>
    /// Megabytes of models taken into the pool a frame at most, nearest the player first, so a burst of models streaming
    /// in is spread over frames rather than stalling one.
    /// </summary>
    [Range(0.25, 1024)]
    public float UploadBudget { get; set; } = 32;

    /// <summary>
    /// Point and spot lights a view lights pixel by pixel at most, nearest first, a power of two; the farther light only
    /// through the light from everywhere. What a view's light tiles hold a bit for each of.
    /// </summary>
    public int LightCapacity { get; set; } = 16384;

    /// <summary>
    /// Texels a side of the pool every shadow is drawn into, a power of two: how many pages of shadow are held at once.
    /// Past it, what is farthest from the player is shadowed coarser.
    /// </summary>
    public int ShadowPool { get; set; } = 8192;

    /// <summary>
    /// Levels of the sun's shadow, each twice as wide as the last: how far it reaches, the finest's width doubled again
    /// for each.
    /// </summary>
    [Range(1, 24)]
    public int ShadowLevels { get; set; } = 16;

    /// <summary>
    /// Metres across the sun's finest level of shadow, sharp close up; 64 pages of 120 texels.
    /// </summary>
    public float ShadowFinest { get; set; } = 8;

    /// <summary>
    /// Metres along the sun a page of its shadow holds either side of the camera: as far as what casts into it can be.
    /// </summary>
    public float ShadowDepth { get; set; } = 4000;

    /// <summary>
    /// Pages of shadow drawn a frame at most, those of the coarsest levels and nearest the player first; the rest wait
    /// a frame, drawn coarser till then.
    /// </summary>
    public int ShadowUpdates { get; set; } = 512;

    /// <summary>
    /// Entries of the table point and spot lights' pages are found in, a power of two, at least twice the pages the views
    /// read of them a frame.
    /// </summary>
    [Browsable(false)]
    public int LightShadowEntries { get; set; } = 65536;

    /// <summary>
    /// The most taps a material's textures are filtered with at a grazing angle: 1 to 16.
    /// </summary>
    [Browsable(false)]
    public float Anisotropy { get; set; } = 8;

    /// <summary>
    /// Levels of the scene's field and probes for global illumination, each twice the size of the last: 1 to 4.
    /// </summary>
    public int GiLevels { get; set; } = 4;

    /// <summary>
    /// Metres a voxel of the finest level: its 64 a side and each level after it twice as far reach as far as global
    /// illumination does.
    /// </summary>
    public float GiVoxel { get; set; } = 0.5f;

    /// <summary>
    /// Rays each probe traces when it is traced: 8 to 256.
    /// </summary>
    public int GiRays { get; set; } = 64;

    /// <summary>
    /// The finest level's probes are traced every this many frames, a share of them each frame, and each coarser level's
    /// half as often: a power of two.
    /// </summary>
    public int GiUpdateEvery { get; set; } = 4;

    /// <summary>
    /// Models whose distance fields are held at once, 64 KB each.
    /// </summary>
    [Browsable(false)]
    public int GiMeshFields { get; set; } = 1024;
}
