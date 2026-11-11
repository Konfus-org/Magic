using System.ComponentModel;
using Magic.Configuration;

namespace Magic.SlangShaderCompiler;

/// <summary>
/// How shaders compile.
/// </summary>
[Category("Debug")]
public sealed record SlangShaderCompilerSettings
{
    /// <summary>
    /// How hard the compiler optimizes shaders, trading the time a load spends compiling against how fast they run on the
    /// GPU: None compiles quickest, High and Maximal run quickest. Each level is cached apart, so a level not used before
    /// compiles every shader once.
    /// </summary>
    [RequireRestart]
    public ShaderOptimization ShaderOptimization { get; init; } = ShaderOptimization.Default;
}

/// <summary>
/// How hard the compiler optimizes, as Slang's levels number them.
/// </summary>
public enum ShaderOptimization
{
    None,
    Default,
    High,
    Maximal,
}
