using System.ComponentModel;
using Magic.Interfaces;

namespace Magic.DxcShaderCompiler;

/// <summary>
/// How DxcShaderCompiler compiles. Set in the project's <c>Presets/Default.preset</c>, by another preset, or with
/// <c>--set DxcShaderCompiler.debugInfo=true</c>.
/// </summary>
[Category("Debug")]
internal sealed class DxcShaderCompilerSettings : ISettings
{
    /// <summary>
    /// Embeds each shader's source and symbols, so a GPU capture (RenderDoc, PIX, Xcode) can step through it. Changed
    /// while running, every shader is compiled again.
    /// </summary>
    public bool HasDebugInfo { get; set; }
}
