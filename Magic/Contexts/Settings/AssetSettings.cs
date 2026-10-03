namespace Magic.Contexts.Settings;

/// <summary>
/// How much of each asset type stays loaded after use, so the next Load of it is free. Every budget is in megabytes,
/// keyed by the asset type's name (<c>"Texture"</c>, or a gem's own <c>"Voxels"</c>; the full name when two share one);
/// 0 keeps none, so every Load of that type reads the file. A name that matches no loaded asset type is warned about.
/// The Chunk budget is also how much of the world stays spawned: chunks that go out of view are kept until the spawned
/// ones together are over it. The defaults suit a machine with 16 GB shared between the CPU and the GPU.
/// </summary>
public sealed class AssetSettings
{
    /// <summary>
    /// Megabytes kept per asset type, by type name. Types not listed get <see cref="DefaultBudget"/>.
    /// </summary>
    public Dictionary<string, int> Budgets { get; set; } = new() { ["Chunk"] = 1024, ["Texture"] = 512, ["Model"] = 256 };

    /// <summary>
    /// Megabytes kept for each asset type not in <see cref="Budgets"/>, each type on its own.
    /// </summary>
    public int DefaultBudget { get; set; } = 32;
}
