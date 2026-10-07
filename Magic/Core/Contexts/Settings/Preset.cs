using Magic.Attributes.Assets;

namespace Magic.Contexts.Assets;

/// <summary>
/// A set of settings values, as data: a <c>.preset</c> file holding a flat pile of <c>"Owner.Property": value</c>,
/// where Owner is a settings class's name (<c>"Lod.Bias": 0.5</c>) or a pass's file name
/// (<c>"ShadowPlan.distance": 120</c>, a number, or <c>[r, g, b, a]</c> for a colour). What it does not name keeps its
/// default. The presets gem applies the one <c>--preset</c> names, else the default, <c>Presets/Default.preset</c>
/// (the project's if it has one). The engine ships Toaster, Default, Fancy and MeltMyGPU.
/// </summary>
[AssetFormat(AssetFormat.Json)]
public sealed class Preset : Asset
{
    /// <summary>
    /// Every value it sets, by <c>"Owner.Property"</c>, ignoring case.
    /// </summary>
    public Dictionary<string, Unread> Values { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
