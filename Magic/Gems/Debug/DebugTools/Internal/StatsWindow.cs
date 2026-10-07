using Magic.Contexts;
using Magic.Contexts.Input;
using Magic.Interfaces;
using Magic.Services;
using System.Text;

namespace DebugToolsGem;

/// <summary>
/// The engine's numbers in one place: everything any system set in <see cref="ITelemetry"/> (frame time and the GC from
/// <see cref="FrameStats"/>, what the transform, render and streaming systems are doing, the renderer's counters, the
/// asset pools), a tab per first part of the name (Frame, Rendering, Streaming), and under it the tab's values as a
/// document: the plain ones first, then a block per sub-category (<c>Frame.Time</c>, <c>Memory.Assets.Texture</c>)
/// headed by its name. Drawn as the "Stats" window while open (F3); it reads the stats and changes none.
/// </summary>
internal sealed class StatsWindow : Window
{
    private readonly List<(string Name, double Value)> _stats = [];
    private readonly List<string> _groups = [];
    private readonly StringBuilder _line = new();

    private string[] _tabs = [];
    private int _tab;
    private readonly Telemetry _telemetry;

    public StatsWindow(IDebugWidgets ui, DebugUI debug, Telemetry telemetry, IInput? input, IClipboard? clipboard) : base("Stats", Key.F3, ui, debug, input, clipboard)
    {
        _telemetry = telemetry;
    }

    protected override void Draw(in Frame frame)
    {
        if (!Open)
            return;

        _stats.Clear();
        _telemetry.CopyTo(_stats);
        Begin(scrollable: true);

        string[] groups = Groups();
        UI.Tabs("##groups", ref _tab, groups);
        if (_tab < groups.Length)
        {
            string text = Contents(groups[_tab]);
            UI.Document("##group", ref text, readOnly: true);
        }

        UI.End();
    }

    protected override string Contents()
    {
        return Contents(null);
    }

    /// <summary>
    /// The stats of <paramref name="group"/> (every group when null), a name and value per line: the ones without a
    /// sub-category first, then each sub-category under a line with its name, indented.
    /// </summary>
    private string Contents(string? group)
    {
        _line.Clear();
        if (group is null)
        {
            foreach ((string name, double value) in _stats)
                _line.Append(name).Append(' ').Append(FrameStats.Format(value)).AppendLine();

            return _line.ToString();
        }

        foreach ((string name, double value) in _stats)
        {
            if (GroupOf(name) == group && SubOf(name).Length == 0)
                _line.Append(NameOf(name)).Append(' ').Append(FrameStats.Format(value)).AppendLine();
        }

        string? sub = null;
        foreach ((string name, double value) in _stats)
        {
            if (GroupOf(name) != group || SubOf(name).Length == 0)
                continue;

            if (sub != SubOf(name))
            {
                sub = SubOf(name);
                if (_line.Length > 0)
                    _line.AppendLine();

                _line.Append(sub).AppendLine();
            }

            _line.Append("  ").Append(NameOf(name)).Append(' ').Append(FrameStats.Format(value)).AppendLine();
        }

        return _line.ToString();
    }

    /// <summary>
    /// The groups among the stats taken this frame, in their order: the same array while they do not change, so
    /// the tabs are not rebuilt every frame.
    /// </summary>
    private string[] Groups()
    {
        _groups.Clear();
        foreach ((string name, _) in _stats)
        {
            string group = GroupOf(name);
            if (_groups.Count == 0 || _groups[^1] != group)
                _groups.Add(group);
        }

        if (!_groups.SequenceEqual(_tabs))
            _tabs = [.. _groups];

        return _tabs;
    }

    /// <summary>
    /// The tab: the part before the first dot.
    /// </summary>
    private static string GroupOf(string name)
    {
        int dot = name.IndexOf('.');
        return dot < 0 ? name : name[..dot];
    }

    /// <summary>
    /// The sub-category: whatever lies between the first and the last dot; empty when there are fewer than two.
    /// </summary>
    private static string SubOf(string name)
    {
        int first = name.IndexOf('.');
        int last = name.LastIndexOf('.');
        return first < 0 || last == first ? "" : name[(first + 1)..last];
    }

    /// <summary>
    /// The name itself: the part after the last dot.
    /// </summary>
    private static string NameOf(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot < 0 ? name : name[(dot + 1)..];
    }

}
