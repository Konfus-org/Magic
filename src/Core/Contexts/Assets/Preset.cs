using System.Text.Json.Nodes;
using Magic.Interfaces;

namespace Magic.Contexts.Assets;

/// <summary>
/// Gem settings in a file, <c>Presets/&lt;Name&gt;.preset</c>: <c>{ "settings": { "SdlPlatform.vsync": false } }</c>.
/// The default one, <c>Presets/Default.preset</c>, is the project's own settings, and any other applied is a kind of
/// machine's over it (<see cref="Services.Settings"/>).
/// </summary>
public sealed class Preset : IJsonAsset<Preset>
{
    public static IReadOnlyList<string> Extensions { get; } = [".preset"];

    public Handle<Preset> Handle { get; init; }

    /// <summary>
    /// Each setting as <c>"Gem.path": value</c>, what <c>--set Gem.path=value</c> says. A value stays JSON, as only the
    /// gem's settings class it lands in gives it a type.
    /// </summary>
    public JsonObject Settings { get; init; } = [];
}
