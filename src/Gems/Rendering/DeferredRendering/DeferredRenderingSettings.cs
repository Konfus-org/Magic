using System.Collections.Immutable;
using System.ComponentModel;
using System.Drawing;
using Magic.Assets;
using Magic.Configuration;
using Magic.Input;

namespace Magic.DeferredRendering;

/// <summary>
/// How the world is drawn, and what the renderer shows of a frame, with its debug window and its keys.
/// </summary>
[Category("Rendering")]
public sealed class DeferredRenderingSettings : ISettings
{
    /// <summary>
    /// The finished picture's size in pixels, with a window or without, which the window shows stretched over it. 0 by
    /// 0 is auto: the window's size, or 1280 by 720 with no window, as it is with a side of 0 or less.
    /// </summary>
    public Size Resolution { get; set; }

    /// <summary>
    /// The part of the resolution the world is drawn at, more than 0 to 1, before the picture is upscaled or stretched
    /// to it. 0 is auto: the most that draws at 1920 by 1080 or less.
    /// </summary>
    public float RenderScale { get; set; }

    /// <summary>
    /// The window camera's vertical field of view in degrees, in place of the camera's own. 0 or less keeps the
    /// camera's.
    /// </summary>
    public float FieldOfView { get; set; }

    /// <summary>
    /// How bright the open sky's light is, as a part of the sun's: what lights a world with no sky entity and what fog
    /// scatters.
    /// </summary>
    public float SkyBrightness { get; set; } = 0.3f;

    /// <summary>
    /// How many seconds a model nothing draws or waits for stays on the GPU before it leaves, to load again when it is
    /// next drawn.
    /// </summary>
    public int ModelKeepSec { get; set; } = 60;

    /// <summary>
    /// How many samples a smoothly filtered texture takes along a surface seen at a slant, 1 to 16: more keeps a floor
    /// sharp into the distance. 1 is off.
    /// </summary>
    [RequireRestart]
    public int Anisotropy { get; set; } = 8;

    /// <summary>
    /// How many of a texture's largest mip levels are left off the GPU, each halving its width and height and saving
    /// about three quarters of its memory: a softer picture for less video memory. A texture keeps at least its smallest
    /// level. 0 keeps every level.
    /// </summary>
    [RequireRestart]
    public int DroppedTextureLevels { get; set; }

    /// <summary>
    /// How many stops brighter the picture is exposed, or darker below 0, when the world has no post processing of its
    /// own: the brightness.
    /// </summary>
    public float ExposureCompensation { get; set; }

    /// <summary>
    /// The post effects left out of every frame, by their material's file name, as Bloom Vignette.
    /// </summary>
    public ImmutableArray<string> DisabledEffects { get; set; } = [];

    /// <summary>
    /// The part of the frame the window shows: Final is the finished frame, any other one part of the GBuffer or the
    /// light alone.
    /// </summary>
    [Category("Debug")]
    public DebugView RenderView { get; set; } = DebugView.Final;

    /// <summary>
    /// Whether the Rendering window, which chooses the view, is open. It needs a gem that provides the debug UI.
    /// </summary>
    [Category("Debug")]
    public bool IsRenderWindowOpen { get; set; }

    /// <summary>
    /// The key that opens and closes the Rendering window.
    /// </summary>
    [Category("Debug")]
    public Control RenderWindowKey { get; set; } = Control.KeyF2;

    /// <summary>
    /// The key that shows the view before the current one.
    /// </summary>
    [Category("Debug")]
    public Control PreviousViewKey { get; set; } = Control.KeyF5;

    /// <summary>
    /// The key that shows the view after the current one.
    /// </summary>
    [Category("Debug")]
    public Control NextViewKey { get; set; } = Control.KeyF6;

    /// <summary>
    /// Metres from the camera at which the Depth view turns black; nearer is lighter, on a log scale.
    /// </summary>
    [Category("Debug")]
    public float DepthViewFar { get; set; } = 1000;

    /// <summary>
    /// How the picture is read where it is stretched over the window or into a screenshot: Nearest keeps each texel a
    /// hard-edged block, Linear blends between them. A gem that upscales takes the picture to the size it is shown at in
    /// place of the stretch.
    /// </summary>
    public TextureFilter UpscaleFilter { get; set; }

    /// <summary>
    /// How bright the picture's middle tones are shown, 0.5 to 2: above 1 lightens them and below 1 darkens them, while
    /// black and white stay.
    /// </summary>
    public float Gamma { get; set; } = 1;
}

/// <summary>
/// What the output shows of a frame's surfaces, the twin of <c>ShowSurface.slang</c>'s <c>DebugView</c>.
/// </summary>
public enum DebugView : uint
{
    /// <summary>
    /// The finished frame.
    /// </summary>
    Final,

    /// <summary>
    /// The surface's base colour.
    /// </summary>
    Albedo,

    /// <summary>
    /// The surface's world normal, each axis from -1 to 1 shown from black to full.
    /// </summary>
    Normal,

    /// <summary>
    /// How far the surface is from the camera, on a log scale: white at the camera.
    /// </summary>
    Depth,

    Roughness,

    Metalness,

    /// <summary>
    /// The material's own occlusion.
    /// </summary>
    Occlusion,

    /// <summary>
    /// How far the surface moved on the screen since last frame.
    /// </summary>
    Velocity,

    /// <summary>
    /// The indirect diffuse light as shading reads it, where something traces it: what a white diffuse surface would
    /// reflect of the light that reaches it indirectly.
    /// </summary>
    IndirectDiffuse,

    /// <summary>
    /// What smooth surfaces reflect, where something traces it at a part of the size, finished by the post effects;
    /// black where nothing is traced.
    /// </summary>
    Reflections,
}
