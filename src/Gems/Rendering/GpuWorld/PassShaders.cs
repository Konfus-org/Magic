using Magic.Assets;
using Magic.Graphics;

namespace Magic.GpuWorld;

/// <summary>
/// The entries the gem's passes run, in the modules of its <c>Shaders/</c> folder, and the library shaders an entry is
/// specialised with.
/// </summary>
internal static class PassShaders
{
    public static ShaderEntry GetEntry(
        AssetManager assets,
        string module,
        string name,
        ShaderStage stage = ShaderStage.Compute
    ) =>
        new()
        {
            Module = assets.Find<Shader>($"Shaders/{module}.slang"),
            Name = name,
            Stage = stage,
        };
}
