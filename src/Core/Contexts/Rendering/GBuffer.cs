using Magic.Contexts.Gpu;
using Magic.Interfaces;

namespace Magic.Contexts.Rendering;

/// <summary>
/// What each pixel's surface is resolved into (<see cref="IRendering.Resolve"/>), each a texture of the view's size in
/// the format the pipeline script gives it: the view's depth, which is read, and the targets the surfaces write, their
/// albedo and occlusion, normal and roughness, metallic and specular, emission, and motion since the last frame. A
/// pass's constants hold it whole as Shaders/Core/GBuffer.slang's <c>GBuffer</c>, member for member, which also says
/// how the targets are packed.
/// </summary>
[ShaderStruct("Shaders/Core/GBuffer.slang", "GBuffer")]
public readonly record struct GBuffer(
    GpuTexture Depth,
    GpuTexture Albedo,
    GpuTexture Normal,
    GpuTexture Surface,
    GpuTexture Emissive,
    GpuTexture Velocity);
