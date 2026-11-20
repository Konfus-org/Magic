using Magic.Configuration;
using Magic.Debugging;
using Magic.Input;
using Magic.Projects;
using Magic.Utils;

namespace Magic.DebugTools;

/// <summary>
/// The gem: it makes <see cref="DebugWindows"/> over its <see cref="Settings"/> and draws them each frame.
/// </summary>
public sealed class EntryPoint : Disposable, IGem
{
    private readonly DebugWindows _debugWindows;

    public EntryPoint(DebugToolsSettings settings, Settings allSettings, IDebugUI ui, IInput? input = null)
    {
        Settings = settings;
        _debugWindows = new DebugWindows(settings, Windows, allSettings, ui, input);
        Track(_debugWindows);
    }

    /// <summary>
    /// The keys of the lines and windows. Game thread.
    /// </summary>
    public DebugToolsSettings Settings { get; }

    /// <summary>
    /// Which lines and windows show, which the keys, the windows' close buttons and the console set. Game thread.
    /// </summary>
    public OpenWindows Windows { get; } = new();

    public void Update(in Frame frame) => _debugWindows.Update();

    // The debug UI's gem takes the frame's lines in its own late update, which runs before this gem's, as this gem
    // needs it.
    public void LateUpdate(in Frame frame) => _debugWindows.ReportCounter();
}
