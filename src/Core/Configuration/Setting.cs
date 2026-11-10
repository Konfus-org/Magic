namespace Magic.Configuration;

/// <summary>
/// One member of a gem's settings as <see cref="Settings.All"/> lists it: its key, its value written the way
/// <see cref="Settings.Set(string, string)"/> reads it, its type, its doc comment's summary (empty when the gem's doc
/// file is missing), and whether changing it restarts the run.
/// </summary>
public readonly record struct Setting(string Key, string Value, Type Type, string Summary, bool RequiresRestart);
