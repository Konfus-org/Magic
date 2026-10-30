using System.Text.Json.Nodes;

namespace Magic.Assets.Types;

/// <summary>
/// Gem settings in a file, <c>Presets/&lt;Name&gt;.preset</c>: <c>{ "settings": { "VulkanGpu.hasVsync": false } }</c>.
/// The default one, <c>Presets/Default.preset</c>, is the project's own settings, and any other applied is a kind of
/// machine's over it.
/// </summary>
[AssetFormat(AssetFileFormat.Json)]
public sealed class Preset : IAsset<Preset>
{
    /// <summary>
    /// Each setting as <c>"Gem.path": value</c>, what <c>--set Gem.path=value</c> says. A value stays JSON, as only the
    /// gem's settings class it lands in gives it a type.
    /// </summary>
    public JsonObject Settings { get; init; } = [];
}
