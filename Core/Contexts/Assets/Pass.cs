using Magic.Attributes;

namespace Magic.Contexts.Assets;

/// <summary>Pixel format of a target a pass creates.</summary>
public enum TargetFormat : byte
{
    Rgba8Srgb,
    Rgba8Unorm,
    Rgba16Float,
    R16Float,
    R32Float,
    Rg16Float,
    B10G11R11Float
}

/// <summary>
/// What a pass writes: an engine target by name (<c>Hdr</c>, <c>Ldr</c>, ...) or a new one, in which case
/// <see cref="Format"/> and <see cref="Scale"/> (of the view size) say what to create.
/// </summary>
public sealed class PassOutput
{
    public string Name { get; set; } = "";

    public TargetFormat Format { get; set; }

    public float Scale { get; set; } = 1f;
}

/// <summary>
/// A custom render pass, as data: a fullscreen fragment shader or a compute shader, the named targets it
/// reads and the one it writes. It runs for the cameras whose <see cref="Components.PostProcessing"/> lists it,
/// where in the list says when, and nowhere else. Inputs bind in order to the
/// shader's textures (with a sampler each) and then its storage buffers; <see cref="Params"/> fill the
/// shader's <c>cbuffer PassParams</c> the way <see cref="Material.Params"/> fill <c>MaterialParams</c>.
/// </summary>
[AssetFormat(AssetFormat.Json)]
public sealed class Pass : Asset
{
    /// <summary>A <c>.frag.hlsl</c> (drawn as a fullscreen triangle) or a <c>.comp.hlsl</c> (dispatched over the output).</summary>
    public Handle<Shader> Shader { get; set; }

    public string[] Inputs { get; set; } = [];

    public PassOutput Output { get; set; } = new();

    public Dictionary<string, Param> Params { get; set; } = [];
}
