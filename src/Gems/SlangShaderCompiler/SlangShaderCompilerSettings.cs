using System.ComponentModel;
using Magic.Configuration;

namespace Magic.SlangShaderCompiler;

/// <summary>
/// How shaders compile.
/// </summary>
[Category("Debug")]
public sealed class SlangShaderCompilerSettings : ISettings
{
    /// <summary>
    /// How hard the compiler optimizes shaders, trading the time a load spends compiling against how fast they run on the
    /// GPU: None compiles quickest, High and Maximal run quickest. Each level is cached apart, so a level not used before
    /// compiles every shader once. The default is None in the Debug configuration and High in the others.
    /// </summary>
    [RequireRestart]
#if DEBUG
    public ShaderOptimization ShaderOptimization { get; set; } = ShaderOptimization.None;
#else
    public ShaderOptimization ShaderOptimization { get; set; } = ShaderOptimization.High;
#endif
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
