using System.Drawing;

namespace Magic.Contexts.Settings;

/// <summary>
/// The renderer's settings: one property per thing that can be turned, each with a default that suits a mid-range
/// discrete GPU. Presets ("low", "ultra") are sets of these values and belong to the project or an editor, not here.
/// Read every frame; changing one that the render state is built from builds it again.
/// </summary>
public sealed class RenderSettings
{
    /// <summary><c>"vulkan"</c>, <c>"direct3d12"</c>, or null for SDL's choice. The <c>SDL_GPU_DRIVER</c> environment variable still wins.</summary>
    public string? Backend { get; set; }

    /// <summary>Present on the display's refresh. On by default, except in a Debug build, where frame times are worth more uncapped.</summary>
#if DEBUG
    public bool Vsync { get; set; }
#else
    public bool Vsync { get; set; } = true;
#endif

    /// <summary>
    /// The resolution a window's scene is rendered at, in pixels: every target of the pipeline (colour, depth, the
    /// culling pyramid, the passes) is this size, and only the finished image is stretched over the window. Empty
    /// (the default) is the window's own size. Render textures keep their own size.
    /// </summary>
    public Size Resolution { get; set; }

    /// <summary>Hide what last frame's depth pyramid covers. Off saves the pyramid and the late pass, for GPUs where they cost more than they cull.</summary>
    public bool OcclusionCulling { get; set; } = true;

    /// <summary>Anisotropic filtering of material textures: 1 is off, 16 the most.</summary>
    public float Anisotropy { get; set; } = 8f;

    /// <summary>
    /// Metres a camera sees: the open domains' chunks its frustum touches within this are loaded. <see cref="float.PositiveInfinity"/>
    /// (<c>"Infinity"</c> in a file or <c>--set</c>) is every chunk in view. The chunks within one chunk size of a camera
    /// are loaded whatever this says and wherever it looks.
    /// </summary>
    public float ViewDist { get; set; } = 500f;

    /// <summary>Keep compiled shaders under the cache folder next to the executable.</summary>
    public bool ShaderCache { get; set; } = true;
}
