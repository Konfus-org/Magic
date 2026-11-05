using System.Diagnostics;
using Magic.Debugging;
using Magic.Events;
using Magic.Input;
using Magic.Projects;

namespace Magic.DebugTools;

/// <summary>
/// The debug UI's lines and windows over what the meters measure, each shown and hidden by its key: the frame rate and
/// memory line (F1), on from the start in a Debug build, and the stats window (F3); or by
/// <c>set DebugTools.counter=true</c> and <c>set DebugTools.stats=true</c>. It listens to the meters only while one
/// shows, and collects what they measured twice a second. They are drawn with <see cref="IDebugUI"/> in Update, so the gem
/// takes the UI and is made after it. Without a gem that provides input no key works. It is public because nothing in its
/// assembly makes it: Magic does, by reflection, when a project lists the gem.
/// </summary>
public sealed class DebugToolsGem(IDebugUI ui, IInput? input = null) : IGem, IDisposable
{
    private const string CounterSetting = "DebugTools.counter";
    private const string StatsSetting = "DebugTools.stats";

    private static readonly TimeSpan _collectEvery = TimeSpan.FromSeconds(0.5);

    private readonly MetricsReader _metrics = new();
    private readonly CounterLine _counter = new(ui);
    private readonly StatsWindow _stats = new(ui);

    /// <summary>
    /// When the meters were last collected, 0 before the first time.
    /// </summary>
    private long _collectedTimestamp;

    public void Dispose() => _metrics.Dispose();

    public void Update(in Frame frame)
    {
        foreach (SettingChanged setting in frame.Events.Get<SettingChanged>())
        {
            if (setting.Key is not CounterSetting and not StatsSetting)
                continue;

            if (!bool.TryParse(setting.Value, out bool isShown))
                Log.Error($"{setting.Key} takes true or false, not '{setting.Value}'.");
            else if (setting.Key == CounterSetting)
                _counter.IsShown = isShown;
            else
                _stats.IsOpen = isShown;
        }

        if (input is not null && input[Control.KeyF1].WasPressed)
            _counter.IsShown = !_counter.IsShown;

        if (input is not null && input[Control.KeyF3].WasPressed)
            _stats.IsOpen = !_stats.IsOpen;

        Collect();
        _counter.Update();
        _stats.Update();
    }

    /// <summary>
    /// Listens while anything shows, and hands it what the meters measured each time it is due.
    /// </summary>
    private void Collect()
    {
        bool isListening = _counter.IsShown || _stats.IsOpen;
        _metrics.Listen(isListening);
        if (!isListening || Stopwatch.GetElapsedTime(_collectedTimestamp) < _collectEvery)
            return;

        _collectedTimestamp = Stopwatch.GetTimestamp();
        _metrics.Collect();
        _counter.Refresh(_metrics.Rows);
        _stats.Refresh(_metrics.Rows);
    }
}
