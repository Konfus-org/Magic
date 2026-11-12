using Magic.Configuration;
using Magic.Debugging;
using Magic.Input;
using Magic.Projects;

namespace Magic.DebugTools;

/// <summary>
/// The gem: it makes <see cref="DebugWindows"/> over its <see cref="Settings"/> and draws them each frame.
/// </summary>
public sealed class EntryPoint(DebugToolsSettings settings, Settings allSettings, IDebugUI ui, IInput? input = null)
    : IGem,
        IDisposable
{
    private readonly DebugWindows _debugWindows = new(settings, allSettings, ui, input);

    /// <summary>
    /// Which lines and windows show, which the keys and the windows' close buttons also set. Game thread.
    /// </summary>
    public DebugToolsSettings Settings { get; } = settings;

    public void Dispose() => _debugWindows.Dispose();

    public void Update(in Frame frame) => _debugWindows.Update();
}
