using Magic.Debugging;

namespace Magic;

/// <summary>
/// What the console sets and reads of how the app runs, under the path <c>App</c>.
/// </summary>
internal interface IRunOptions
{
    /// <inheritdoc cref="App.IsHeadless"/>
    bool IsHeadless { get; }

    /// <inheritdoc cref="App.ShouldExitOnError"/>
    bool ShouldExitOnError { get; set; }

    /// <summary>
    /// How much the log prints.
    /// </summary>
    Log.VerbosityLevel Verbosity { get; set; }
}
