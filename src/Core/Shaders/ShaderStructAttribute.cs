namespace Magic.Shaders;

/// <summary>
/// Marks a C# struct as the twin of a Slang struct of the same name: 32-bit scalars, their vectors, row-major
/// matrices and heap references only, laid out as the GPU reads them. The renderer checks each against the shader's
/// reflection as it starts, so a field out of place is a start-up error with both layouts, never a corrupt frame.
/// </summary>
[AttributeUsage(AttributeTargets.Struct)]
public sealed class ShaderStructAttribute : Attribute;
