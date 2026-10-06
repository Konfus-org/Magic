using Magic.Contexts.Settings;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace DeferredRendererGem;

/// <summary>
/// The deferred renderer's own settings, all read live: anisotropy makes the texture sampler again, the shader cache is
/// asked at every compile. How the scene is lit is the pipeline's passes' own parameters (<c>Resources/Passes/Core</c>).
/// </summary>
[Settings("Deferred")]
[Category("Display/Renderer")]
public sealed class DeferredSettings
{
    /// <summary>
    /// The size a window's scene is rendered at, as a share of the window's: every target of the pipeline (colour,
    /// depth, the culling pyramid, the passes) is that size, and only the finished image is stretched over the window,
    /// so its shape is always the window's. Render textures keep their own size.
    /// </summary>
    [DisplayName("Render scale")]
    [Description("The scene is drawn at this share of the window's size, then stretched over it: below 1 is faster and softer, above 1 sharper and slower.")]
    [Range(0.25, 2)]
    public float Scale { get; set; } = 1f;

    /// <summary>
    /// Anisotropic filtering of material textures: 1 is off, 16 the most.
    /// </summary>
    [DisplayName("Texture anisotropy")]
    [Description("Anisotropic filtering of material textures: 1 is off, 16 the sharpest at grazing angles.")]
    [Range(1, 16)]
    public float Anisotropy { get; set; } = 8f;

    /// <summary>
    /// Keep compiled shaders under the cache folder next to the executable.
    /// </summary>
    [Description("Keep compiled shaders in the cache folder next to the executable, so the next start skips compiling.")]
    public bool ShaderCache { get; set; } = true;

    /// <summary>
    /// In a debugging renderer, compare what the GPU culled with a CPU cull of the same instances once a second or
    /// so, and log an error when they disagree. It reads the GPU back and walks every instance, which stalls the
    /// frame it runs in (tens of milliseconds with a hundred thousand instances), so it is off unless asked for:
    /// <c>--set Deferred.CullingCheck=true</c>, for a run that is there to check.
    /// </summary>
    [Category("Debug/Renderer")]
    [Description("Debug builds: once a second, check the GPU cull against a CPU cull and log disagreements. Stalls the frame it runs in.")]
    public bool CullingCheck { get; set; }

    /// <summary>
    /// Whether the world's post-processing runs; off, the lit scene goes to the screen as it is.
    /// </summary>
    [Category("Debug/Renderer")]
    [DisplayName("Post-processing")]
    [Description("Run the world's post-processing passes; off shows the lit scene as it comes out of the lighting.")]
    public bool PostProcessing { get; set; } = true;

    /// <summary>
    /// What is shown in place of the lit scene: the gbuffer's parts, the lighting's inputs, the GI's volumes, or the
    /// level of detail every instance is drawn at (white the full mesh, then green, yellow, orange, red for the impostor).
    /// Shown as it is: no post-processing runs over it.
    /// </summary>
    [Category("Debug/Renderer")]
    [DisplayName("Debug view")]
    [Description("Show one part of the picture instead of the lit scene: the gbuffer, a lighting input, the GI, or the level of detail (white full, green, yellow, orange, red impostor).")]
    public DebugView DebugView { get; set; }
}
