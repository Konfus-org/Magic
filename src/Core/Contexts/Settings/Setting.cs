using System.Text.Json.Nodes;

namespace Magic.Contexts.Settings;

/// <summary>
/// One gem setting and its value, as <c>--set</c>, a <see cref="Assets.Preset"/> or a change while running gives it.
/// </summary>
/// <param name="Key">The gem, as the project lists it, then where the setting sits in its settings, with dots between
/// levels: <c>SdlPlatform.vsync</c>.</param>
/// <param name="Value">The value as JSON, which only the gem's settings class gives a type.</param>
public readonly record struct Setting(string Key, JsonNode? Value);
