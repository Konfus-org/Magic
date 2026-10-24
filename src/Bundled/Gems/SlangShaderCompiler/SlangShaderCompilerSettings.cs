using System.ComponentModel;
using Magic.Interfaces;

namespace Magic.SlangShaderCompiler;

/// <summary>
/// How SlangShaderCompiler compiles. Set in the project's <c>Presets/Default.preset</c>, by another preset, or with
/// <c>--set SlangShaderCompiler.hasDebugInfo=true</c>.
/// </summary>
[Category("Debug")]
internal sealed class SlangShaderCompilerSettings : ISettings
{
    /// <summary>
    /// Embeds each shader's source and symbols, so a GPU capture (RenderDoc, PIX, Xcode) can step through it. Changed
    /// while running, every shader is compiled again.
    /// </summary>
    public bool HasDebugInfo { get; set; }
}
