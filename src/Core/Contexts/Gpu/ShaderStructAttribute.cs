using Magic.Interfaces;

namespace Magic.Contexts.Gpu;

/// <summary>
/// Marks a C# struct as the twin of a struct a shader declares: the one named <paramref name="name"/> in the module at
/// <paramref name="path"/>, such as <c>Shaders/Core/GBuffer.slang</c>. The two have the same fields, by name and in
/// order, at the same offsets, and the same size. A renderer checks its own and Core's as it starts, by the shader's
/// reflection (<see cref="IShaderCompiler.ReflectStructAsync"/>).
/// </summary>
[AttributeUsage(AttributeTargets.Struct, Inherited = false)]
public sealed class ShaderStructAttribute(string path, string name) : Attribute
{
    public string Path { get; } = path;

    public string Name { get; } = name;
}
