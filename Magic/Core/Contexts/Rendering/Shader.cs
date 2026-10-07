namespace Magic.Contexts.Assets;

/// <summary>
/// Which stage a shader file is, from its suffix. <see cref="Include"/> files are only ever included; a
/// <see cref="Surface"/> file (<c>.surf.hlsl</c>) declares a material's parameters and surface function and
/// is stitched into the renderer's own pipelines rather than compiled on its own.
/// </summary>
public enum ShaderStage : byte
{
    Vertex,
    Fragment,
    Compute,
    Surface,
    Include
}

/// <summary>
/// HLSL source, as written. The shader asset is text rather than bytecode because the target format (SPIR-V
/// or DXIL) is only known once a device exists, so compiling belongs to the render gem, which also caches the
/// result. <c>Hlsl.Includes</c> lists the files the source includes, so that editing an include can reload every
/// shader using it.
/// </summary>
public sealed class Shader : TextAsset
{
    /// <summary>
    /// From the file name: <c>.vert.hlsl</c>, <c>.frag.hlsl</c>, <c>.comp.hlsl</c>, <c>.surf.hlsl</c>; anything else (<c>.hlsli</c>) is an include.
    /// </summary>
    public ShaderStage Stage => Path switch
    {
        _ when Path.EndsWith(".vert.hlsl", StringComparison.OrdinalIgnoreCase) => ShaderStage.Vertex,
        _ when Path.EndsWith(".frag.hlsl", StringComparison.OrdinalIgnoreCase) => ShaderStage.Fragment,
        _ when Path.EndsWith(".comp.hlsl", StringComparison.OrdinalIgnoreCase) => ShaderStage.Compute,
        _ when Path.EndsWith(".surf.hlsl", StringComparison.OrdinalIgnoreCase) => ShaderStage.Surface,
        _ => ShaderStage.Include
    };
}
