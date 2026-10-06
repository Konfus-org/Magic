using Magic.Contexts;
using Magic.Attributes.Scripts;
using Magic.Interfaces;
using Magic.Utils;
using Magic.Contexts.Debug;

namespace DebugToolsGem;

/// <summary>
/// The list down the left of the main window: every log message written with <c>onScreen: true</c> and every
/// <see cref="Debugging.UI.Warning"/> and <see cref="Debugging.UI.Error"/> without a position, each for its seconds,
/// oldest first. A message logged again while it shows is one line with how often. Shown while
/// <see cref="Debugging.UI.Enabled"/>: always in a Debug build, in a Release build only while the debug UI is on.
/// </summary>
[Phase(UpdateType.Overlay)]
internal sealed class ScreenLog : ISystem
{
    private readonly List<DebugEntry> _entries = [];
    private readonly List<DebugLine> _lines = [];

    public void Dispose()
    {
    }

    public void Run(in Frame frame)
    {
        // Taken whether shown or not: what has had its time has to go either way.
        _entries.Clear();
        Debugging.UI.Entries.Take(Debugging.UI.Now, positioned: false, _entries);
        if (_entries.Count == 0 || !Debugging.UI.Enabled)
            return;

        _lines.Clear();
        foreach (DebugEntry entry in _entries)
            _lines.Add(new DebugLine(entry.Count > 1 ? $"{entry.Text} (x{entry.Count})" : entry.Text, entry.Color));

        Debugging.UI.Lines(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_lines));
    }
}
