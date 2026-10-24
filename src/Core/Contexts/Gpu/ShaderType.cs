using System.IO;
using Magic.Contexts.Assets;

namespace Magic.Contexts.Gpu;

/// <summary>
/// A type a shader declares: the one named <paramref name="Name"/> in <paramref name="Module"/>, or, with no module,
/// one the entry point's own module reaches through its imports.
/// </summary>
public readonly record struct ShaderType(Handle<Shader> Module, string Name)
{
    /// <summary>
    /// The struct a surface's or a sky's module declares, named like its file at <paramref name="path"/>:
    /// Shaders/Surfaces/Pbr.slang's Pbr.
    /// </summary>
    public static ShaderType Of(Handle<Shader> module, string path)
        => new(module, Path.GetFileNameWithoutExtension(path));
}
