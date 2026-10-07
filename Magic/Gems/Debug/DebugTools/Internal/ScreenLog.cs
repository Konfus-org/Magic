using Magic.Contexts;
using Magic.Contexts.Debug;
using Magic.Interfaces;
using Magic.Services;
using System.Runtime.InteropServices;

namespace DebugToolsGem;

/// <summary>
/// The list down the left of the main window: every <see cref="DebugUI.Info"/>, <see cref="DebugUI.Warning"/> and
/// <see cref="DebugUI.Error"/> without a position, each for its seconds,
/// oldest first. A message logged again while it shows is one line with how often. Shown while
/// <see cref="DebugUI.Enabled"/>: always in a Debug build, in a Release build only while the debug UI is on.
/// </summary>
internal sealed class ScreenLog(IDebugWidgets ui, DebugUI debug) : ISystem
{
    private readonly List<DebugEntry> _entries = [];
    private readonly List<DebugLine> _lines = [];

    public void Dispose()
    {
    }

    public UpdateType Phase => UpdateType.Overlay;

    public void Run(in Frame frame)
    {
        // Taken whether shown or not: what has had its time has to go either way.
        _entries.Clear();
        debug.Take(positioned: false, _entries);
        if (_entries.Count == 0 || !debug.Enabled)
            return;

        _lines.Clear();
        foreach (DebugEntry entry in _entries)
            _lines.Add(new DebugLine(entry.Count > 1 ? $"{entry.Text} (x{entry.Count})" : entry.Text, entry.Color));

        ui.Lines(CollectionsMarshal.AsSpan(_lines));
    }
}
