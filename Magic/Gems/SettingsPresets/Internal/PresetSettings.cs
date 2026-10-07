using Magic.Contexts.Settings;
using System.ComponentModel;

namespace SettingsPresetsGem;

/// <summary>
/// The preset applied at start: <c>--preset</c> sets <see cref="Path"/>.
/// </summary>
[Settings("Preset")]
[Browsable(false)] // read once at start: no knob for the settings window
internal sealed class PresetSettings
{
    /// <summary>
    /// A <c>.preset</c> among the assets (a gem's or the project's); empty takes the default, <c>Presets/Default.preset</c>.
    /// </summary>
    public string Path { get; set; } = "";
}
