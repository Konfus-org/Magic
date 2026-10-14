using Magic.Interfaces;

namespace Magic.Contexts.Gpu;

/// <summary>
/// The bytecode a GPU takes (<see cref="IGpu.ShaderFormat"/>), which <see cref="IShaderCompiler"/> compiles to.
/// </summary>
public enum GpuShaderFormat : byte
{
    /// <summary>
    /// SPIR-V 1.3, for Vulkan 1.1.
    /// </summary>
    Spirv,

    /// <summary>
    /// DXIL, Shader Model 6.0, for Direct3D 12.
    /// </summary>
    Dxil,

    /// <summary>
    /// Metal Shading Language source, for Metal.
    /// </summary>
    Msl,
}
