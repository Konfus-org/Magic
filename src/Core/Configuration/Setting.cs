namespace Magic.Configuration;

/// <summary>
/// One member of the settings of a gem, as <see cref="Settings.Options"/> lists it.
/// </summary>
/// <remarks>
/// It holds the key, the category at the start of the key, the value, the declared type, the summary of the doc
/// comment, and whether a change restarts the run. The summary is empty when the doc file of the gem is missing.
/// </remarks>
public readonly record struct Setting(
    string Key,
    string Category,
    object? Value,
    Type Type,
    string Summary,
    bool RequiresRestart
);
