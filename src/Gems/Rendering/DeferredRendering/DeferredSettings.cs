using System.ComponentModel;
using Magic.Assets;
using Magic.Input;

namespace Magic.DeferredRendering;

/// <summary>
/// What the renderer shows of a frame, and its debug window with its keys.
/// </summary>
[Category("Debug")]
public sealed record DeferredSettings
{
    /// <summary>
    /// The part of the frame the window shows: Final is the finished frame, any other one part of the GBuffer or the
    /// light alone.
    /// </summary>
    public DebugView RenderView { get; init; } = DebugView.Final;

    /// <summary>
    /// Whether the Rendering window, which chooses the view, is open. It needs a gem that provides the debug UI.
    /// </summary>
    public bool IsRenderWindowOpen { get; init; }

    /// <summary>
    /// The key that opens and closes the Rendering window.
    /// </summary>
    public Control RenderWindowKey { get; init; } = Control.KeyF2;

    /// <summary>
    /// The key that shows the view before the current one.
    /// </summary>
    public Control PreviousViewKey { get; init; } = Control.KeyF5;

    /// <summary>
    /// The key that shows the view after the current one.
    /// </summary>
    public Control NextViewKey { get; init; } = Control.KeyF6;

    /// <summary>
    /// Metres from the camera at which the Depth view turns black; nearer is lighter, on a log scale.
    /// </summary>
    public float DepthViewFar { get; init; } = 1000;

    /// <summary>
    /// How the picture is read where it is stretched over the window or into a screenshot: Nearest keeps each texel a
    /// hard-edged block, Linear blends between them. A gem that upscales takes the picture to the size it is shown at in
    /// place of the stretch.
    /// </summary>
    [Category("Rendering")]
    public TextureFilter UpscaleFilter { get; init; }

    /// <summary>
    /// How bright the picture's middle tones are shown, 0.5 to 2: above 1 lightens them and below 1 darkens them, while
    /// black and white stay.
    /// </summary>
    [Category("Rendering")]
    public float Gamma { get; init; } = 1;
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
