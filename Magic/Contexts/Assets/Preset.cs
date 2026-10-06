using Magic.Attributes.Assets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Magic.Contexts.Assets;

/// <summary>
/// A set of settings values, as data: a <c>.preset</c> file holding a flat pile of <c>"Owner.Property": value</c>,
/// where Owner is a settings class's name (<c>"Lod.Bias": 0.5</c>) or a pass's file name
/// (<c>"ShadowPlan.distance": 120</c>, a number, or <c>[r, g, b, a]</c> for a colour). What it does not name keeps its
/// default. The project names one (<c>"preset"</c> in the <c>.magic</c> file, <c>--preset</c> overrides it); without
/// one the engine's <c>Presets/Normal.preset</c> is used. The engine ships Toaster, Normal, Fancy and MeltMyGPU.
/// </summary>
[AssetFormat(AssetFormat.Json)]
public sealed class Preset : Asset
{
    /// <summary>
    /// The engine's own default, under Resources.
    /// </summary>
    public const string DefaultPath = "Presets/Normal.preset";

    [JsonExtensionData]
    public Dictionary<string, JsonElement> Values { get; set; } = [];
}
