namespace Magic.Configuration;

/// <summary>
/// One member of a gem's settings as <see cref="Settings.Options"/> lists it: its key, the category the key starts
/// with, its value, the type it is declared as, its doc comment's summary (empty when the gem's doc file is missing),
/// and whether changing it restarts the run.
/// </summary>
public readonly record struct Setting(
    string Key,
    string Category,
    object? Value,
    Type Type,
    string Summary,
    bool RequiresRestart
);
