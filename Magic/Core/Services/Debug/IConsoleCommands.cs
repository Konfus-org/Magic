using Magic.Utils;

namespace Magic.Interfaces;

/// <summary>
/// Console commands by name: the console runs one when its name is typed, with the rest of the line as arguments.
/// Main thread.
/// </summary>
public interface IConsoleCommands
{
    /// <summary>
    /// Adds <paramref name="name"/>, replacing a command of that name, until the handle is disposed (a gem does in its
    /// Dispose).
    /// </summary>
    Subscription Add(string name, Action<string[]> run);
}
