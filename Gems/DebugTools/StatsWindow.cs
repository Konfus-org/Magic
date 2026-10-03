using Magic.Contexts;
using Magic.Contexts.Input;
using Magic.Interfaces;
using Magic.Utils;
using System.Globalization;
using System.Text;

namespace DebugToolsGem;

/// <summary>
/// The engine's own numbers in one place: frames per second and frame time, the GC, and everything any system set in
/// <see cref="Debugging.Stats"/> (what the transform, render and streaming systems are doing, the renderer's counters,
/// the asset pools), grouped by the first part of the name: a tab per group, the group's values as a document under
/// it. Drawn as the "Stats" window while open (F3), and logged (verbose, so only with --verbose) every
/// <see cref="LogIntervalMs"/> whether open or not, so a headless or scripted run can have them too. Reads the stats,
/// changes nothing but its own.
/// </summary>
internal sealed class StatsWindow : Window
{
    public const double LogIntervalMs = 30000;

    private readonly List<(string Name, double Value)> _stats = [];
    private readonly List<string> _groups = [];
    private readonly StringBuilder _line = new();

    private string[] _tabs = [];
    private int _tab;

    private double _frameMs;
    private double _lastFrameMs;
    private double _windowWorstMs;
    private double _sinceLogMs;
    private double _sinceGcSampleMs;
    private long _sampledAllocatedBytes;
    private TimeSpan _sampledPause;
    private readonly int[] _sampledCollections = new int[3];

    public StatsWindow(IInput? input, IClipboard? clipboard) : base("Stats", Key.F3, input, clipboard)
    {
    }

    protected override void Draw(in Frame frame)
    {
        double dtMs = frame.Delta * 1000d;
        _frameMs = _frameMs == 0 ? dtMs : _frameMs + ((dtMs - _frameMs) * 0.05);
        _windowWorstMs = Math.Max(_windowWorstMs, dtMs);

        double fps = _frameMs > 0 ? 1000 / _frameMs : 0;
        Debugging.Stats.Set("Frame.Ms", _frameMs);
        Debugging.Stats.Set("Frame.Fps", fps);
        Debugging.Stats.Set("Frame.WorstMs", _lastFrameMs);
        SampleGc(dtMs);

        _stats.Clear();
        Debugging.Stats.Take(_stats);

        if (Open)
        {
            Begin(scrollable: true);

            string[] groups = Groups();
            Debugging.UI.Tabs("##groups", ref _tab, groups);
            if (_tab < groups.Length)
            {
                string text = Contents(groups[_tab]);
                Debugging.UI.Document("##group", ref text, readOnly: true);
            }

            Debugging.UI.End();
        }

        _sinceLogMs += dtMs;
        if (_sinceLogMs >= LogIntervalMs)
        {
            _sinceLogMs = 0;
            _line.Clear();
            foreach ((string name, double value) in _stats)
                _line.Append(_line.Length == 0 ? "" : ", ").Append(name).Append(' ').Append(Format(value));

            Debugging.Log.Verbose($"Stats: {_line}.");
        }

        if (fps < 30)
            Debugging.Log.Verbose("FPS is below 30! Consider profiling and optimizing.");

        _lastFrameMs = _windowWorstMs;
        _windowWorstMs = 0;
    }

    /// <summary>
    /// Rates over the last second rather than per frame, so they are readable: allocation in MB/s, collections per
    /// generation per second, and the share of wall time the GC had threads paused.
    /// </summary>
    private void SampleGc(double dtMs)
    {
        _sinceGcSampleMs += dtMs;
        if (_sinceGcSampleMs < 1000)
            return;

        long allocated = GC.GetTotalAllocatedBytes();
        TimeSpan pause = GC.GetTotalPauseDuration();
        double seconds = _sinceGcSampleMs / 1000d;
        Debugging.Stats.Set("GC.HeapMB", Megabytes(GC.GetTotalMemory(false)));
        Debugging.Stats.Set("GC.AllocatedMBPerSecond", Megabytes(allocated - _sampledAllocatedBytes) / seconds);
        Debugging.Stats.Set("GC.PausedPercent", (pause - _sampledPause).TotalMilliseconds / _sinceGcSampleMs * 100);
        for (int generation = 0; generation < _sampledCollections.Length; generation++)
        {
            int count = GC.CollectionCount(generation);
            Debugging.Stats.Set($"GC.Gen{generation}Collections", count);
            Debugging.Stats.Set($"GC.Gen{generation}PerSecond", (count - _sampledCollections[generation]) / seconds);
            _sampledCollections[generation] = count;
        }

        _sampledAllocatedBytes = allocated;
        _sampledPause = pause;
        _sinceGcSampleMs = 0;
    }

    protected override string Contents()
    {
        return Contents(null);
    }

    /// <summary>
    /// The stats of <paramref name="group"/> (every group when null), a name and value per line.
    /// </summary>
    private string Contents(string? group)
    {
        _line.Clear();
        foreach ((string name, double value) in _stats)
        {
            string part = GroupOf(name);
            if (group is null)
                _line.Append(name).Append(' ').Append(Format(value)).AppendLine();
            else if (part == group)
                _line.Append(name, part.Length + 1, name.Length - part.Length - 1).Append(' ').Append(Format(value)).AppendLine();
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

    private static string GroupOf(string name)
    {
        int dot = name.IndexOf('.');
        return dot < 0 ? name : name[..dot];
    }

    /// <summary>
    /// Whole numbers as they are, anything else to two places.
    /// </summary>
    private static string Format(double value)
    {
        return value == Math.Floor(value) && Math.Abs(value) < 1e15
            ? value.ToString("0", CultureInfo.InvariantCulture)
            : value.ToString("0.00", CultureInfo.InvariantCulture);
    }

    private static double Megabytes(long bytes)
    {
        return bytes / (1024d * 1024d);
    }
}
