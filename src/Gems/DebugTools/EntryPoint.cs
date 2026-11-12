using Magic.Configuration;
using Magic.Debugging;
using Magic.Input;
using Magic.Projects;

namespace Magic.DebugTools;

/// <summary>
/// The gem: it makes <see cref="DebugWindows"/> and draws them each frame. It takes the debug UI, so it is made after
/// the gem that provides it. It is public because nothing in its assembly makes it: Magic does, by reflection, when a
/// project lists the gem.
/// </summary>
public sealed class EntryPoint(DebugToolsSettings settings, Settings allSettings, IDebugUI ui, IInput? input = null)
    : IGem,
        IDisposable
{
    /// <summary>
    /// The debug lines and windows, with their settings.
    /// </summary>
    public DebugWindows DebugWindows { get; } = new(settings, allSettings, ui, input);

    public void Dispose() => DebugWindows.Dispose();

    public void Update(in Frame frame) => DebugWindows.Update();
}
