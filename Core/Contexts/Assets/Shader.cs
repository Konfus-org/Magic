using Magic.Attributes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Magic.Contexts.Assets;

/// <summary>Which stage a shader file is, from its suffix; <see cref="Include"/> files are only ever included.</summary>
public enum ShaderStage : byte
{
    Vertex,
    Fragment,
    Compute,
    Include
}

/// <summary>
/// HLSL source, as written. The shader asset is text rather than bytecode because the target format (SPIR-V
/// or DXIL) is only known once a device exists, so compiling belongs to the render gem, which also caches the
/// result. <see cref="Includes"/> lists the files the source includes, as written (relative to the shader
/// root), so that editing an include can reload every shader using it.
/// </summary>
[AssetFormat(AssetFormat.Text)]
public sealed partial class Shader : Asset
{
    public string Text { get; set; } = "";

    public string EntryPoint { get; set; } = "main";

    /// <summary>From the file name: <c>.vert.hlsl</c>, <c>.frag.hlsl</c>, <c>.comp.hlsl</c>; anything else (<c>.hlsli</c>) is an include.</summary>
    [JsonIgnore]
    public ShaderStage Stage => Path switch
    {
        _ when Path.EndsWith(".vert.hlsl", StringComparison.OrdinalIgnoreCase) => ShaderStage.Vertex,
        _ when Path.EndsWith(".frag.hlsl", StringComparison.OrdinalIgnoreCase) => ShaderStage.Fragment,
        _ when Path.EndsWith(".comp.hlsl", StringComparison.OrdinalIgnoreCase) => ShaderStage.Compute,
        _ => ShaderStage.Include
    };

    [JsonIgnore]
    public string[] Includes => [.. IncludePattern().Matches(Text).Select(m => m.Groups[1].Value)];

    [GeneratedRegex("""^\s*#\s*include\s+"([^"]+)"\s*$""", RegexOptions.Multiline)]
    private static partial Regex IncludePattern();
}
