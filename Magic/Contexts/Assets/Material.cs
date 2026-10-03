using Magic.Attributes.Assets;

namespace Magic.Contexts.Assets;

/// <summary>
/// How a material's surface is composited; it picks the pipeline the renderer draws it with.
/// </summary>
public enum MaterialType : byte
{
    Opaque,

    /// <summary>
    /// Opaque, but fragments the surface shader declares transparent below its cutoff are discarded.
    /// </summary>
    Masked,

    /// <summary>
    /// Blended over what is behind it, drawn after everything opaque.
    /// </summary>
    Transparent
}

/// <summary>
/// A surface shader plus the values of the parameters it declares. The shader (a <c>.surf.hlsl</c>) declares
/// <c>struct MaterialParams</c>; the renderer reads that declaration, packs <see cref="Params"/> into the
/// record the shader loads, and stitches the shader's surface function into its own pipelines. Every
/// distinct shader is one pipeline class, so a GPU-driven renderer still draws all materials of one shader
/// in one indirect call. A <c>.mat</c> file is this class as JSON (<see cref="AssetJson"/>); keys of
/// <see cref="Params"/> are the HLSL member names, and a parameter a file leaves out takes the shader's
/// default. The sidecar carries only the id.
/// </summary>
[AssetFormat(AssetFormat.Json)]
public sealed class Material : Asset
{
    public Handle<Shader> Shader { get; set; }

    public MaterialType Type { get; set; }

    public bool DoubleSided { get; set; }

    public Dictionary<string, Param> Params { get; set; } = [];
}
