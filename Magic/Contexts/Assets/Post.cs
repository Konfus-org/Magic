using Magic.Attributes.Assets;

namespace Magic.Contexts.Assets;

/// <summary>
/// What a post writes: an engine target by name (<c>Hdr</c>, <c>Ldr</c>, ...) or a new one, in which case
/// <see cref="Format"/> and <see cref="Scale"/> (of the render target's size) say what to create.
/// </summary>
public sealed class PostOutput
{
    public string Name { get; set; } = "";

    public PassFormat Format { get; set; }

    public float Scale { get; set; } = 1f;
}

/// <summary>
/// A post-processing pass, as data: a <c>.post</c> file naming a fullscreen fragment shader or a compute shader, the
/// targets it reads and the one it writes. It runs after the lighting, once over each render target, when the world's
/// <see cref="Components.PostProcessing"/> lists it, where in the list says when, and not otherwise. Inputs bind in
/// order to the shader's textures, each with a sampler, declared for it by the renderer (<c>Hdr</c> and
/// <c>HdrSampler</c>); <see cref="Params"/> fill the shader's <c>struct PassParams</c> the way <see cref="Material.Params"/>
/// fill <c>MaterialParams</c>. The simple form of a <see cref="Pass"/>, for the passes a project writes most.
/// </summary>
[AssetFormat(AssetFormat.Json)]
public sealed class Post : Asset
{
    /// <summary>
    /// A <c>.frag.hlsl</c> (drawn as a fullscreen triangle) or a <c>.comp.hlsl</c> (dispatched over the output).
    /// </summary>
    public Handle<Shader> Shader { get; set; }

    public string[] Inputs { get; set; } = [];

    public PostOutput Output { get; set; } = new();

    public Dictionary<string, Param> Params { get; set; } = [];
}
