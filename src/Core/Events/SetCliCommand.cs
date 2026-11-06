namespace Magic.Events;

/// <summary>
/// The console's <c>set Key=value</c> command, split at the <c>=</c>: the gem the key names reads it from the frame's
/// events and does what the value says. Nothing keeps the value. Published once per line typed.
/// </summary>
public readonly record struct SetCliCommand(string Key, string Value);
