using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using Magic.Contexts.Frames;
using Magic.Contexts.Settings;
using Magic.Contexts.Subscriptions;
using Magic.Contexts.Telemetry;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.LogMetrics;

/// <summary>
/// Logs what the engine measures: every <see cref="LogMetricsSettings.IntervalSec"/>, a line per instrument and set
/// of tags measured since the last time, then the slowest frame and where its time went, and once more when the run
/// ends. Off unless <see cref="LogMetricsSettings.IsEnabled"/>, and then it does not listen at all, so nothing is
/// measured for it.
/// <para>
/// Measurements are added up on the thread that records them, the main or render thread for Core's, without allocating
/// after a series' first, so listening is cheap in the frame loop; the lines are formatted in <see cref="Update"/>, on
/// the main thread, once per interval. A change to the settings while running starts or stops listening, or starts it
/// over under the meters they now name.
/// </para>
/// </summary>
internal sealed class LogMetricsGem : IGem, IDisposable
{
    private readonly LogMetricsSettings _settings;
    private readonly Subscription _settingsChanges;
    private readonly Lock _lock = new();
    private readonly List<Measured> _measured = [];
    private readonly SlowestFrame _slowest = new();
    private MeterListener? _listener;

    /// <summary>
    /// The meters listened to, as the settings said when listening started.
    /// </summary>
    private IReadOnlyList<string> _meters = [];

    /// <summary>
    /// When the measurements being added up began: the last report, or the start.
    /// </summary>
    private long _windowStartTimestamp;

    public LogMetricsGem(LogMetricsSettings settings, Events events)
    {
        _settings = settings;
        _settingsChanges = events.Watch<SettingsChanged>(FollowSettings);
        if (!settings.IsEnabled)
        {
            Debugging.Verbose("Metrics are not logged; --set LogMetrics.isEnabled=true turns them on.");
        }
        else if (settings.IntervalSec <= 0)
        {
            Debugging.Error($"LogMetrics's intervalSec must be more than 0, not {settings.IntervalSec}; "
                + "metrics are not logged.");
        }

        if (IsWanted(settings)) Start();
    }

    public void Dispose()
    {
        _settingsChanges.Dispose();
        Stop();
    }

    public void Update(in Frame frame)
    {
        _slowest.Current = frame.Index;

        if (_listener is null || Stopwatch.GetElapsedTime(_windowStartTimestamp).TotalSeconds < _settings.IntervalSec)
            return;

        Report();
    }

    /// <summary>
    /// Reports what was measured since the last report, then stops listening and forgets the instruments: as the run
    /// ends, on the main thread once the last frame is done, or when the settings turn logging off.
    /// </summary>
    public void Stop()
    {
        if (_listener is null) return;

        Report();
        _listener.Dispose();
        _listener = null;
    }

    /// <summary>
    /// Which part of the slowest frame an instrument feeds, by the name Core records it under.
    /// </summary>
    private static Role GetRole(Instrument instrument) => instrument.Name switch
    {
        Metrics.FrameDuration => Role.Frame,
        Metrics.HookDuration => Role.Hook,
        Metrics.WorkDuration => Role.Work,
        _ => Role.Other,
    };

    /// <summary>
    /// <paramref name="value"/> in <paramref name="unit"/>, with seconds shown as milliseconds: most of what is timed
    /// in a frame takes far less than one.
    /// </summary>
    private static string FormatAmount(double value, string? unit) => unit switch
    {
        "s" => string.Create(CultureInfo.InvariantCulture, $"{value * 1000:0.###} ms"),
        null or "" => value.ToString("0.###", CultureInfo.InvariantCulture),
        _ => string.Create(CultureInfo.InvariantCulture, $"{value:0.###} {unit}"),
    };

    /// <summary>
    /// Whether <paramref name="settings"/> ask for metrics to be logged.
    /// </summary>
    private static bool IsWanted(LogMetricsSettings settings) => settings.IsEnabled && settings.IntervalSec > 0;

    /// <summary>
    /// Listens as the settings now say: starts when they turn logging on, stops when they turn it off, and starts over
    /// when the meters listened to change, so a change while running takes from the next interval.
    /// </summary>
    private void FollowSettings(SettingsChanged changed)
    {
        if (changed is not { Before: LogMetricsSettings before, After: LogMetricsSettings after }) return;

        if (_listener is not null
            && (!IsWanted(after) || !before.Meters.SequenceEqual(after.Meters, StringComparer.Ordinal)))
        {
            Stop();
        }

        if (IsWanted(after) && _listener is null) Start();
    }

    private void Start()
    {
        MeterListener listener = new() { InstrumentPublished = Publish, MeasurementsCompleted = Complete };
        listener.SetMeasurementEventCallback<byte>(Add);
        listener.SetMeasurementEventCallback<short>(Add);
        listener.SetMeasurementEventCallback<int>(Add);
        listener.SetMeasurementEventCallback<long>(Add);
        listener.SetMeasurementEventCallback<float>(Add);
        listener.SetMeasurementEventCallback<double>(Add);
        listener.SetMeasurementEventCallback<decimal>(Add);
        _meters = _settings.Meters;
        _listener = listener;
        _windowStartTimestamp = Stopwatch.GetTimestamp();
        listener.Start();
    }

    /// <summary>
    /// Adds one measurement to its instrument's series for its tags, and Core's frame, hook and handed-work times to
    /// the slowest frame. Runs on the thread that recorded it, often the main thread in the middle of a frame.
    /// </summary>
    private void Add<T>(
        Instrument instrument,
        T measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        object? state)
        where T : struct, INumber<T>
    {
        if (state is not Measured measured) return;

        double value = double.CreateTruncating(measurement);
        measured.Add(value, tags);
        switch (measured.Role)
        {
            case Role.Hook:
                _slowest.Add(value, tags);
                break;
            case Role.Work:
                _slowest.AddWork(value, tags);
                break;
            case Role.Frame:
                _slowest.Ended(value);
                break;
            case Role.Other:
            default:
                break;
        }
    }

    /// <summary>
    /// Logs a line per instrument and set of tags measured since the last report, then the slowest frame, and starts
    /// them over.
    /// </summary>
    private void Report()
    {
        // The window starts over, and the observable instruments are read now, so the report has their latest values.
        double windowSec = Stopwatch.GetElapsedTime(_windowStartTimestamp).TotalSeconds;
        _windowStartTimestamp = Stopwatch.GetTimestamp();
        _listener?.RecordObservableInstruments();
        lock (_lock)
        {
            foreach (Measured measured in _measured)
            {
                measured.Report(windowSec);
            }
        }

        _slowest.Report();
    }

    /// <summary>
    /// Listens to a new instrument when its meter is one of the settings' <see cref="LogMetricsSettings.Meters"/>,
    /// noting which of Core's the slowest frame is built from.
    /// </summary>
    private void Publish(Instrument instrument, MeterListener listener)
    {
        if (!IsListening(instrument.Meter.Name)) return;

        Measured measured = new(instrument, GetRole(instrument));
        lock (_lock)
        {
            _measured.Add(measured);
        }

        listener.EnableMeasurementEvents(instrument, measured);
    }

    /// <summary>
    /// Forgets an instrument that has ended, so a gem that is unloaded is not kept.
    /// </summary>
    private void Complete(Instrument instrument, object? state)
    {
        if (state is not Measured measured) return;

        lock (_lock)
        {
            _measured.Remove(measured);
        }
    }

    /// <summary>
    /// Whether <paramref name="meter"/> is one of the settings' meters or under one: "Magic" takes "Magic.Core" and
    /// every "Magic.Gems.&lt;Gem&gt;", not "Magical".
    /// </summary>
    private bool IsListening(string meter)
    {
        foreach (string name in _meters)
        {
            if (meter.StartsWith(name, StringComparison.Ordinal)
                && (meter.Length == name.Length || meter[name.Length] == '.'))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// What an instrument is to the slowest frame, besides its own line.
    /// </summary>
    private enum Role
    {
        /// <summary>
        /// Nothing: it only gets its line.
        /// </summary>
        Other,

        /// <summary>
        /// Core's frame time, which ends the frame being timed.
        /// </summary>
        Frame,

        /// <summary>
        /// Core's gem hook time, one of the frame's hooks.
        /// </summary>
        Hook,

        /// <summary>
        /// Core's handed-work time, under the thread that ran it.
        /// </summary>
        Work,
    }

    /// <summary>
    /// How an instrument's measurements are summed up in its line.
    /// </summary>
    private enum Summary
    {
        /// <summary>
        /// Histograms: how many, and their mean, least and most.
        /// </summary>
        Distribution,

        /// <summary>
        /// Counters: the total, and per second.
        /// </summary>
        Total,

        /// <summary>
        /// Gauges and observable instruments: the latest value, since an observable counter's is already a total.
        /// </summary>
        Latest,
    }

    /// <summary>
    /// One instrument's measurements since the last report, a series per set of tags. Any thread may record one, so
    /// it is locked.
    /// </summary>
    private sealed class Measured(Instrument instrument, Role role)
    {
        private readonly Lock _lock = new();
        private readonly List<Series> _series = [];
        private readonly Summary _summary = GetSummary(instrument);

        internal Role Role => role;

        internal void Add(double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            lock (_lock)
            {
                Find(tags).Add(value);
            }
        }

        /// <summary>
        /// Logs a line for each series measured over the last <paramref name="windowSec"/>, and starts it over.
        /// </summary>
        internal void Report(double windowSec)
        {
            lock (_lock)
            {
                foreach (Series series in _series)
                {
                    if (series.Count == 0) continue;

                    Debugging.Info(Describe(series, windowSec));
                    series.Clear();
                }
            }
        }

        private static Summary GetSummary(Instrument instrument)
        {
            if (instrument.IsObservable) return Summary.Latest;

            Type kind = instrument.GetType().GetGenericTypeDefinition();

            if (kind == typeof(Histogram<>)) return Summary.Distribution;

            return kind == typeof(Counter<>) || kind == typeof(UpDownCounter<>) ? Summary.Total : Summary.Latest;
        }

        /// <summary>
        /// The series for <paramref name="tags"/>, added the first time they are seen: the one allocation.
        /// </summary>
        private Series Find(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            foreach (Series series in _series)
            {
                if (series.Matches(tags)) return series;
            }

            Series added = new(tags.ToArray());
            _series.Add(added);

            return added;
        }

        private string Describe(Series series, double windowSec)
        {
            string? unit = instrument.Unit;
            StringBuilder text = new(instrument.Name);
            ReadOnlySpan<KeyValuePair<string, object?>> tags = series.Tags;
            for (int i = 0; i < tags.Length; i++)
            {
                text.Append(i == 0 ? " {" : ", ").Append(tags[i].Key).Append('=').Append(tags[i].Value);
            }

            text.Append(tags.Length > 0 ? "}: " : ": ");
            switch (_summary)
            {
                case Summary.Distribution:
                    text.Append(
                        CultureInfo.InvariantCulture,
                        $"{series.Count} in {windowSec:0.##} s, "
                            + $"mean {FormatAmount(series.Sum / series.Count, unit)}, "
                            + $"min {FormatAmount(series.Min, unit)}, max {FormatAmount(series.Max, unit)}");
                    break;
                case Summary.Total:
                    text.Append(
                        CultureInfo.InvariantCulture,
                        $"{FormatAmount(series.Sum, unit)} in {windowSec:0.##} s, "
                            + $"{FormatAmount(series.Sum / windowSec, unit)}/s");
                    break;
                case Summary.Latest:
                default:
                    text.Append(FormatAmount(series.Last, unit));
                    break;
            }

            return text.ToString();
        }
    }

    /// <summary>
    /// What one instrument measured with one set of tags since the last report.
    /// </summary>
    private sealed class Series(KeyValuePair<string, object?>[] tags)
    {
        internal ReadOnlySpan<KeyValuePair<string, object?>> Tags => tags;

        internal long Count { get; private set; }

        internal double Sum { get; private set; }

        internal double Min { get; private set; }

        internal double Max { get; private set; }

        internal double Last { get; private set; }

        internal void Add(double value)
        {
            if (Count == 0)
            {
                Min = value;
                Max = value;
            }
            else
            {
                Min = Math.Min(Min, value);
                Max = Math.Max(Max, value);
            }

            Last = value;
            Sum += value;
            Count++;
        }

        internal void Clear()
        {
            // The least and the most start over from the next value, which comes with a count of 0.
            Count = 0;
            Sum = 0;
        }

        /// <summary>
        /// Whether <paramref name="other"/> are this series' tags, in the same order: an instrument is recorded with
        /// its tags in one order.
        /// </summary>
        internal bool Matches(ReadOnlySpan<KeyValuePair<string, object?>> other)
        {
            if (other.Length != tags.Length) return false;

            for (int i = 0; i < other.Length; i++)
            {
                if (!string.Equals(other[i].Key, tags[i].Key, StringComparison.Ordinal)
                    || !Equals(other[i].Value, tags[i].Value))
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// The slowest frame since the last report, and where its time went: the gem hooks Core timed in it, the time
    /// outside them (the frame loop, waiting for the render thread, listeners, the first call's JIT), and how long the
    /// garbage collector paused it, which counts in whichever of those it paused. The render thread's hooks count in
    /// the main thread's frame they ran beside, and are recorded from that thread, so it is locked.
    /// </summary>
    private sealed class SlowestFrame
    {
        /// <summary>
        /// How many of the slowest frame's hooks are named, slowest first.
        /// </summary>
        private const int HooksShown = 3;

        private readonly Lock _lock = new();

        /// <summary>
        /// The frame being timed and the slowest since the last report, a double buffer: a frame that ends slower than
        /// the slowest swaps places with it, and whichever is then the frame being timed starts over.
        /// </summary>
        private FrameTimes _timing = new();
        private FrameTimes _slowest = new();
        private int _count;
        private double _sumSec;

        /// <summary>
        /// How long the garbage collector had paused the process for when the last frame ended.
        /// </summary>
        private TimeSpan _pausedBefore = GC.GetTotalPauseDuration();

        /// <summary>
        /// The frame whose time Core records next: the one whose <see cref="Update"/> ran last.
        /// </summary>
        internal ulong Current { get; set; }

        /// <summary>
        /// Keeps a hook's time in the frame being timed.
        /// </summary>
        internal void Add(double tookSec, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            Hook hook = new(FindTag(tags, Metrics.GemNameTag), FindTag(tags, Metrics.FramePhaseTag), tookSec);
            lock (_lock)
            {
                _timing.Hooks.Add(hook);
            }
        }

        /// <summary>
        /// Keeps a piece of handed work's time in the frame being timed, under the thread that ran it.
        /// </summary>
        internal void AddWork(double workSec, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            bool isRender = FindTag(tags, Metrics.ThreadNameTag) is "render";
            lock (_lock)
            {
                if (isRender) _timing.RenderWorkSec += workSec;
                else _timing.MainWorkSec += workSec;
            }
        }

        /// <summary>
        /// Ends the frame being timed, which took <paramref name="frameSec"/>, keeping it as the slowest when it is the
        /// slowest yet.
        /// </summary>
        internal void Ended(double frameSec)
        {
            TimeSpan paused = GC.GetTotalPauseDuration();
            lock (_lock)
            {
                _timing.Index = Current;
                _timing.Sec = frameSec;
                _timing.Paused = paused - _pausedBefore;
                if (_count == 0 || frameSec > _slowest.Sec) (_slowest, _timing) = (_timing, _slowest);

                _timing.Clear();
                _pausedBefore = paused;
                _sumSec += frameSec;
                _count++;
            }
        }

        /// <summary>
        /// Logs the slowest frame since the last report, and starts over.
        /// </summary>
        internal void Report()
        {
            lock (_lock)
            {
                if (_count == 0) return;

                FrameTimes slowest = _slowest;
                slowest.Hooks.Sort(static (first, second) => second.TookSec.CompareTo(first.TookSec));
                StringBuilder text = new();
                text.Append(
                    CultureInfo.InvariantCulture,
                    $"Slowest frame f{slowest.Index} took "
                        + $"{FormatAmount(slowest.Sec, "s")}, {slowest.Sec * _count / _sumSec:0.#}x the mean:");
                double hookedSec = 0;
                for (int i = 0; i < slowest.Hooks.Count; i++)
                {
                    Hook hook = slowest.Hooks[i];
                    // A render-thread hook ran beside the main thread's frame rather than inside it.
                    if (hook.Phase is not nameof(Phase.Render)) hookedSec += hook.TookSec;

                    if (i < HooksShown)
                    {
                        text.Append(
                            CultureInfo.InvariantCulture,
                            $" {hook.Gem}.{hook.Phase} {FormatAmount(hook.TookSec, "s")},");
                    }
                }

                text.Append(" outside the main thread's hooks ").Append(FormatAmount(slowest.Sec - hookedSec, "s"));
                if (slowest.MainWorkSec > 0)
                {
                    text.Append(", of it work handed to the main thread ")
                        .Append(FormatAmount(slowest.MainWorkSec, "s"));
                }

                if (slowest.RenderWorkSec > 0)
                    text.Append("; work handed to the render thread ").Append(FormatAmount(slowest.RenderWorkSec, "s"));

                text.Append("; GC paused ").Append(FormatAmount(slowest.Paused.TotalSeconds, "s"));
                Debugging.Info(text.ToString());
                _count = 0;
                _sumSec = 0;
            }
        }

        private static object? FindTag(ReadOnlySpan<KeyValuePair<string, object?>> tags, string key)
        {
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (string.Equals(tag.Key, key, StringComparison.Ordinal)) return tag.Value;
            }

            return null;
        }

        /// <summary>
        /// One gem hook's time, named by the tags Core records it with.
        /// </summary>
        private readonly record struct Hook(object? Gem, object? Phase, double TookSec);

        /// <summary>
        /// One frame's times: which it is, how long it took, how long the garbage collector paused it, its hooks, and
        /// how long each engine thread spent on work handed to it.
        /// </summary>
        private sealed class FrameTimes
        {
            internal ulong Index { get; set; }

            internal double Sec { get; set; }

            internal TimeSpan Paused { get; set; }

            internal List<Hook> Hooks { get; } = [];

            internal double MainWorkSec { get; set; }

            internal double RenderWorkSec { get; set; }

            /// <summary>
            /// Starts over, to time the next frame.
            /// </summary>
            internal void Clear()
            {
                Hooks.Clear();
                MainWorkSec = 0;
                RenderWorkSec = 0;
            }
        }
    }
}
