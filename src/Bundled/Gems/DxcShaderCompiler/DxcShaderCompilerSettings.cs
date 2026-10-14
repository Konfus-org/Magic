namespace Magic.DxcShaderCompiler;

/// <summary>
/// How DxcShaderCompiler compiles. Set in the project's <c>Presets/Default.preset</c>, by another preset, or with
/// <c>--set DxcShaderCompiler.debugInfo=true</c>.
/// </summary>
internal sealed class DxcShaderCompilerSettings
{
    /// <summary>
    /// Embeds each shader's source and symbols, so a GPU capture (RenderDoc, PIX, Xcode) can step through it.
    /// </summary>
    public bool DebugInfo { get; set; }
}
