namespace Magic.Events;

/// <summary>
/// A setting typed into the console, <c>set Key=value</c>: the gem the key names reads it from the frame's events
/// and does what the value says. Published once per line typed.
/// </summary>
public readonly record struct SettingChanged(string Key, string Value);
