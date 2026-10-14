using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Numerics;
using System.Text;
using Magic.Contexts.Frames;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.LogMetrics;

/// <summary>
/// Logs what the engine measures: every <see cref="LogMetricsSettings.IntervalSeconds"/>, a line per instrument and set
/// of tags measured since the last time, then the slowest frame and where its time went, and once more when the run
/// ends. Off unless <see cref="LogMetricsSettings.Enabled"/>, and then it does not listen at all, so nothing is
/// measured for it.
/// <para>
/// Measurements are added up on the thread that records them, the main or render thread for Core's, without allocating
/// after a series' first, so listening is cheap in the frame loop; the lines are formatted in <see cref="Update"/>, on
/// the main thread, once per interval.
/// </para>
/// </summary>
internal sealed class LogMetricsGem : IGem, IDisposable
{
    private readonly LogMetricsSettings _settings;
    private readonly MeterListener _listener = new();
    private readonly Lock _lock = new();
    private readonly List<Measured> _measured = [];
    private readonly SlowestFrame _slowest = new();
    private readonly bool _isListening;

    /// <summary>
    /// When the measurements being added up began: the last report, or the start.
    /// </summary>
    private long _windowStart;

    /// <summary>
    /// Core's <c>magic.frame.duration</c>, recorded as each frame ends, once it is listened to.
    /// </summary>
    private Instrument? _frameDuration;

    /// <summary>
    /// Core's <c>magic.gem.hook.duration</c>, recorded after each gem's hook, once it is listened to.
    /// </summary>
    private Instrument? _hookDuration;

    public LogMetricsGem(LogMetricsSettings settings)
    {
        _settings = settings;
        if (!settings.Enabled)
        {
            Debugging.Verbose("Metrics are not logged; --set LogMetrics.enabled=true turns them on.");
            return;
        }

        if (settings.IntervalSeconds <= 0)
        {
            Debugging.Error($"LogMetrics's intervalSeconds must be more than 0, not {settings.IntervalSeconds}; metrics are not logged.");
            return;
        }

        _listener.InstrumentPublished = Publish;
        _listener.MeasurementsCompleted = Complete;
        _listener.SetMeasurementEventCallback<byte>(Add);
        _listener.SetMeasurementEventCallback<short>(Add);
        _listener.SetMeasurementEventCallback<int>(Add);
        _listener.SetMeasurementEventCallback<long>(Add);
        _listener.SetMeasurementEventCallback<float>(Add);
        _listener.SetMeasurementEventCallback<double>(Add);
        _listener.SetMeasurementEventCallback<decimal>(Add);
        _windowStart = Stopwatch.GetTimestamp();
        _listener.Start();
        _isListening = true;
    }

    public void Dispose()
    {
        if (_isListening)
        {
            Report();
        }

        _listener.Dispose();
    }

    public void Update(in Frame frame)
    {
        _slowest.Current = frame.Index;
        if (_isListening && Stopwatch.GetElapsedTime(_windowStart).TotalSeconds >= _settings.IntervalSeconds)
        {
            Report();
        }
    }

    /// <summary>
    /// Adds one measurement to its instrument's series for its tags, and Core's frame and hook times to the slowest
    /// frame. Runs on the thread that recorded it, often the main thread in the middle of a frame.
    /// </summary>
    private void Add<T>(Instrument instrument, T measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
        where T : struct, INumber<T>
    {
        if (state is not Measured measured)
        {
            return;
        }

        double value = double.CreateTruncating(measurement);
        measured.Add(value, tags);
        if (instrument == _hookDuration)
        {
            _slowest.Add(value, tags);
        }
        else if (instrument == _frameDuration)
        {
            _slowest.Ended(value);
        }
    }

    /// <summary>
    /// Logs a line per instrument and set of tags measured since the last report, then the slowest frame, and starts
    /// them over.
    /// </summary>
    private void Report()
    {
        double seconds = Stopwatch.GetElapsedTime(_windowStart).TotalSeconds;
        _windowStart = Stopwatch.GetTimestamp();
        _listener.RecordObservableInstruments();
        lock (_lock)
        {
            foreach (Measured measured in _measured)
            {
                measured.Report(seconds);
            }
        }

        _slowest.Report();
    }

    /// <summary>
    /// Listens to a new instrument when its meter is one of the settings' <see cref="LogMetricsSettings.Meters"/>, and
    /// keeps Core's frame and hook times apart for the slowest frame.
    /// </summary>
    private void Publish(Instrument instrument, MeterListener listener)
    {
        if (!Listens(instrument.Meter.Name))
        {
            return;
        }

        Measured measured = new(instrument);
        lock (_lock)
        {
            _measured.Add(measured);
        }

        if (string.Equals(instrument.Name, "magic.frame.duration", StringComparison.Ordinal))
        {
            _frameDuration = instrument;
        }
        else if (string.Equals(instrument.Name, "magic.gem.hook.duration", StringComparison.Ordinal))
        {
            _hookDuration = instrument;
        }

        listener.EnableMeasurementEvents(instrument, measured);
    }

    /// <summary>
    /// Forgets an instrument that has ended, so a gem that is unloaded is not kept.
    /// </summary>
    private void Complete(Instrument instrument, object? state)
    {
        if (state is not Measured measured)
        {
            return;
        }

        lock (_lock)
        {
            _measured.Remove(measured);
        }
    }

    /// <summary>
    /// Whether <paramref name="meter"/> is one of the settings' meters or under one: "Magic" takes "Magic" and
    /// "Magic.Renderer", not "Magical".
    /// </summary>
    private bool Listens(string meter)
    {
        foreach (string name in _settings.Meters)
        {
            if (meter.StartsWith(name, StringComparison.Ordinal) && (meter.Length == name.Length || meter[name.Length] == '.'))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// <paramref name="value"/> in <paramref name="unit"/>, with seconds shown as milliseconds: most of what is timed in
    /// a frame takes far less than one.
    /// </summary>
    private static string Amount(double value, string? unit) => unit switch
    {
        "s" => string.Create(CultureInfo.InvariantCulture, $"{value * 1000:0.###} ms"),
        null or "" => value.ToString("0.###", CultureInfo.InvariantCulture),
        _ => string.Create(CultureInfo.InvariantCulture, $"{value:0.###} {unit}"),
    };

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
    private sealed class Measured(Instrument instrument)
    {
        private readonly Lock _lock = new();
        private readonly List<Series> _series = [];
        private readonly Summary _summary = SummaryOf(instrument);

        public void Add(double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            lock (_lock)
            {
                Find(tags).Add(value);
            }
        }

        /// <summary>
        /// Logs a line for each series measured over the last <paramref name="seconds"/>, and starts it over.
        /// </summary>
        public void Report(double seconds)
        {
            lock (_lock)
            {
                foreach (Series series in _series)
                {
                    if (series.Count == 0)
                    {
                        continue;
                    }

                    Debugging.Info(Describe(series, seconds));
                    series.Clear();
                }
            }
        }

        private static Summary SummaryOf(Instrument instrument)
        {
            if (instrument.IsObservable)
            {
                return Summary.Latest;
            }

            Type kind = instrument.GetType().GetGenericTypeDefinition();
            if (kind == typeof(Histogram<>))
            {
                return Summary.Distribution;
            }

            return kind == typeof(Counter<>) || kind == typeof(UpDownCounter<>) ? Summary.Total : Summary.Latest;
        }

        /// <summary>
        /// The series for <paramref name="tags"/>, added the first time they are seen: the one allocation.
        /// </summary>
        private Series Find(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            foreach (Series series in _series)
            {
                if (series.Matches(tags))
                {
                    return series;
                }
            }

            Series added = new(tags.ToArray());
            _series.Add(added);
            return added;
        }

        private string Describe(Series series, double seconds)
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
                    text.Append(CultureInfo.InvariantCulture, $"{series.Count} in {seconds:0.##} s, mean {Amount(series.Sum / series.Count, unit)}, min {Amount(series.Min, unit)}, max {Amount(series.Max, unit)}");
                    break;
                case Summary.Total:
                    text.Append(CultureInfo.InvariantCulture, $"{Amount(series.Sum, unit)} in {seconds:0.##} s, {Amount(series.Sum / seconds, unit)}/s");
                    break;
                case Summary.Latest:
                default:
                    text.Append(Amount(series.Last, unit));
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
        public ReadOnlySpan<KeyValuePair<string, object?>> Tags => tags;

        public long Count { get; private set; }

        public double Sum { get; private set; }

        public double Min { get; private set; }

        public double Max { get; private set; }

        public double Last { get; private set; }

        public void Add(double value)
        {
            Min = Count == 0 ? value : Math.Min(Min, value);
            Max = Count == 0 ? value : Math.Max(Max, value);
            Last = value;
            Sum += value;
            Count++;
        }

        public void Clear()
        {
            Count = 0;
            Sum = 0;
        }

        /// <summary>
        /// Whether <paramref name="other"/> are this series' tags, in the same order: an instrument is recorded with
        /// its tags in one order.
        /// </summary>
        public bool Matches(ReadOnlySpan<KeyValuePair<string, object?>> other)
        {
            if (other.Length != tags.Length)
            {
                return false;
            }

            for (int i = 0; i < other.Length; i++)
            {
                if (!string.Equals(other[i].Key, tags[i].Key, StringComparison.Ordinal) || !Equals(other[i].Value, tags[i].Value))
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
    /// garbage collector paused it, which counts in whichever of those it paused. The render thread's hooks count in the
    /// main thread's frame they ran beside, and are recorded from that thread, so it is locked.
    /// </summary>
    private sealed class SlowestFrame
    {
        /// <summary>
        /// How many of the slowest frame's hooks are named, slowest first.
        /// </summary>
        private const int HooksShown = 3;

        private readonly Lock _lock = new();
        private readonly List<Hook> _hooks = [];
        private readonly List<Hook> _slowestHooks = [];
        private int _count;
        private double _sum;
        private ulong _index;
        private double _seconds;
        private TimeSpan _paused;

        /// <summary>
        /// How long the garbage collector had paused the process for when the last frame ended.
        /// </summary>
        private TimeSpan _pausedBefore = GC.GetTotalPauseDuration();

        /// <summary>
        /// The frame whose time Core records next: the one whose <see cref="LogMetricsGem.Update"/> ran last.
        /// </summary>
        public ulong Current { get; set; }

        /// <summary>
        /// Keeps a hook's time in the frame being timed.
        /// </summary>
        public void Add(double seconds, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            Hook hook = new(Tag(tags, "magic.gem.name"), Tag(tags, "magic.frame.phase"), seconds);
            lock (_lock)
            {
                _hooks.Add(hook);
            }
        }

        /// <summary>
        /// Ends the frame being timed, which took <paramref name="seconds"/>, keeping its hooks when it is the slowest
        /// yet.
        /// </summary>
        public void Ended(double seconds)
        {
            TimeSpan paused = GC.GetTotalPauseDuration();
            lock (_lock)
            {
                if (_count == 0 || seconds > _seconds)
                {
                    _index = Current;
                    _seconds = seconds;
                    _paused = paused - _pausedBefore;
                    _slowestHooks.Clear();
                    _slowestHooks.AddRange(_hooks);
                }

                _pausedBefore = paused;
                _sum += seconds;
                _count++;
                _hooks.Clear();
            }
        }

        /// <summary>
        /// Logs the slowest frame since the last report, and starts over.
        /// </summary>
        public void Report()
        {
            lock (_lock)
            {
                if (_count == 0)
                {
                    return;
                }

                _slowestHooks.Sort(static (first, second) => second.Seconds.CompareTo(first.Seconds));
                StringBuilder text = new();
                text.Append(CultureInfo.InvariantCulture, $"Slowest frame f{_index} took {Amount(_seconds, "s")}, {_seconds * _count / _sum:0.#}x the mean:");
                double hooked = 0;
                for (int i = 0; i < _slowestHooks.Count; i++)
                {
                    Hook hook = _slowestHooks[i];
                    if (!IsRenderThread(hook.Phase))
                    {
                        hooked += hook.Seconds;
                    }

                    if (i < HooksShown)
                    {
                        text.Append(CultureInfo.InvariantCulture, $" {hook.Gem}.{hook.Phase} {Amount(hook.Seconds, "s")},");
                    }
                }

                text.Append(" outside the main thread's hooks ").Append(Amount(_seconds - hooked, "s")).Append("; GC paused ").Append(Amount(_paused.TotalSeconds, "s"));
                Debugging.Info(text.ToString());
                _count = 0;
                _sum = 0;
            }
        }

        /// <summary>
        /// Whether a hook ran on the render thread, beside the main thread's frame rather than inside it.
        /// </summary>
        private static bool IsRenderThread(object? phase) => phase is nameof(Phase.Render);

        private static object? Tag(ReadOnlySpan<KeyValuePair<string, object?>> tags, string key)
        {
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (string.Equals(tag.Key, key, StringComparison.Ordinal))
                {
                    return tag.Value;
                }
            }

            return null;
        }

        /// <summary>
        /// One gem hook's time, named by the tags Core records it with.
        /// </summary>
        private readonly record struct Hook(object? Gem, object? Phase, double Seconds);
    }
}
