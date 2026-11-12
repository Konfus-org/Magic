using Magic.Assets;

namespace Magic.Graphics;

/// <summary>
/// Entries for a gem's passes, from the modules in its <c>Shaders/</c> folder.
/// </summary>
public static class PassShaders
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
