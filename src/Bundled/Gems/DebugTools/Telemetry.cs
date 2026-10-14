using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Magic.DebugTools;

/// <summary>
/// What the meters listened to measure, summed up each second as lines laid out as the instruments are named, as an
/// exporter shows them: a meter's together, in the order the settings list the meters, and in one an area's, the part
/// of the name after its first dot, so <c>dotnet.gc.collections</c> is <c>collections</c> in the area <c>gc</c>; a name
/// of two parts is its own area. An instrument measured without tags is one line; with tags, a line of its name, then
/// one per set of tags, showing their values. A histogram's line is how many a second, their mean and most, so the
/// frame's duration says the frame rate, its time and the slowest frame; a counter's is its total over the second and
/// its rate; a running total's, such as the garbage collector's, the total and how fast it rose; a gauge's its latest
/// value. Seconds are shown as milliseconds, bytes as megabytes, and a count's unit, such as <c>{collection}</c>, not at
/// all, as the name says what is counted.
/// <para>
/// It listens only between <see cref="Listen"/> on and off. Measurements are added up on the thread that records
/// them, without allocating after a series' first, so listening is cheap in the frame loop; the lines are made on the
/// main thread, once a second.
/// </para>
/// </summary>
internal sealed class Telemetry(DebugToolsSettings settings) : IDisposable
{
    private readonly Lock _lock = new();

    /// <summary>
    /// The instruments listened to, kept in the order their lines show: the meters as the settings list them, a meter's
    /// together and, in one, an area's, each in the order they were first published.
    /// </summary>
    private readonly List<Measured> _measured = [];

    private readonly List<TelemetryLine> _lines = [];
    private string[] _meters = [];
    private MeterListener? _listener;

    /// <summary>
    /// When the second being summed up began.
    /// </summary>
    private long _secondStart;

    /// <summary>
    /// The last second's lines, in the order they show.
    /// </summary>
    public IReadOnlyList<TelemetryLine> Lines => _lines;

    /// <summary>
    /// The meters of the last second's lines, in their order.
    /// </summary>
    public ReadOnlySpan<string> Meters => _meters;

    public void Dispose()
    {
        _listener?.Dispose();
        _listener = null;
    }

    /// <summary>
    /// Starts or stops listening. Stopped, it forgets what it measured.
    /// </summary>
    public void Listen(bool listening)
    {
        if (listening == (_listener is not null))
        {
            return;
        }

        if (_listener is { } listener)
        {
            listener.Dispose();
            _listener = null;
            _lines.Clear();
            _meters = [];
            return;
        }

        _listener = new MeterListener { InstrumentPublished = Publish, MeasurementsCompleted = Complete };
        _listener.SetMeasurementEventCallback<byte>(Add);
        _listener.SetMeasurementEventCallback<short>(Add);
        _listener.SetMeasurementEventCallback<int>(Add);
        _listener.SetMeasurementEventCallback<long>(Add);
        _listener.SetMeasurementEventCallback<float>(Add);
        _listener.SetMeasurementEventCallback<double>(Add);
        _listener.SetMeasurementEventCallback<decimal>(Add);
        _secondStart = Stopwatch.GetTimestamp();
        _listener.Start();
    }

    /// <summary>
    /// Once a second has gone by since the last time: the lines made from what it measured, and the next second
    /// started.
    /// </summary>
    public void Refresh()
    {
        double seconds = Stopwatch.GetElapsedTime(_secondStart).TotalSeconds;
        if (_listener is not { } listener || seconds < 1)
        {
            return;
        }

        _secondStart = Stopwatch.GetTimestamp();
        listener.RecordObservableInstruments();
        _lines.Clear();
        lock (_lock)
        {
            foreach (Measured measured in _measured)
            {
                measured.Describe(_lines, seconds);
            }
        }

        _meters = MetersOf(_lines);
    }

    /// <summary>
    /// Every line as one text, under its meter's name and its area's.
    /// </summary>
    public string AllText()
    {
        StringBuilder text = new();
        string meter = string.Empty;
        string area = string.Empty;
        foreach (TelemetryLine line in _lines)
        {
            if (!string.Equals(line.Meter, meter, StringComparison.Ordinal))
            {
                meter = line.Meter;
                area = string.Empty;
                text.Append(text.Length == 0 ? string.Empty : "\n").Append("# ").Append(meter).Append('\n');
            }

            if (!string.Equals(line.Area, area, StringComparison.Ordinal))
            {
                area = line.Area;
                text.Append(area).Append('\n');
            }

            text.Append("  ").Append(line.Text).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>
    /// The meters of <paramref name="lines"/>, which keep each meter's together, in their order.
    /// </summary>
    private static string[] MetersOf(List<TelemetryLine> lines)
    {
        List<string> meters = [];
        foreach (TelemetryLine line in lines)
        {
            if (meters.Count == 0 || !string.Equals(meters[^1], line.Meter, StringComparison.Ordinal))
            {
                meters.Add(line.Meter);
            }
        }

        return [.. meters];
    }

    /// <summary>
    /// <paramref name="value"/> in <paramref name="unit"/>, with seconds shown as milliseconds, as most of what is timed
    /// in a frame takes far less than one, bytes as megabytes, and a count's unit, in braces, left out.
    /// </summary>
    private static string Amount(double value, string? unit) => unit switch
    {
        "s" => string.Create(CultureInfo.InvariantCulture, $"{value * 1000:0.###} ms"),
        "By" => string.Create(CultureInfo.InvariantCulture, $"{value / (1024 * 1024):0.#} MB"),
        null or "" or ['{', ..] => value.ToString("0.###", CultureInfo.InvariantCulture),
        _ => string.Create(CultureInfo.InvariantCulture, $"{value:0.###} {unit}"),
    };

    /// <summary>
    /// One measurement into its instrument's series for its tags, on the thread that recorded it.
    /// </summary>
    private static void Add<T>(Instrument instrument, T measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
        where T : struct, INumber<T>
    {
        if (state is Measured measured)
        {
            measured.Add(double.CreateTruncating(measurement), tags);
        }
    }

    /// <summary>
    /// Listens to a new instrument when its meter is one of the settings' meters, as they are when listening starts, or
    /// under one: "Magic" takes "Magic" and "Magic.Renderer", not "Magical".
    /// </summary>
    private void Publish(Instrument instrument, MeterListener listener)
    {
        string meter = instrument.Meter.Name;
        IReadOnlyList<string> names = settings.Meters;
        for (int order = 0; order < names.Count; order++)
        {
            string name = names[order];
            if (!meter.StartsWith(name, StringComparison.Ordinal) || (meter.Length != name.Length && meter[name.Length] != '.'))
            {
                continue;
            }

            Measured measured = new(instrument, order);
            lock (_lock)
            {
                _measured.Insert(PlaceOf(measured), measured);
            }

            listener.EnableMeasurementEvents(instrument, measured);
            return;
        }
    }

    /// <summary>
    /// Where <paramref name="added"/> goes among the instruments listened to, to keep their order: after the last of its
    /// meter and area, else after the last of its meter, else before the first whose meter the settings list later.
    /// </summary>
    private int PlaceOf(Measured added)
    {
        int meterEnd = -1;
        int listedLater = _measured.Count;
        for (int i = _measured.Count - 1; i >= 0; i--)
        {
            Measured listed = _measured[i];
            if (listed.Order > added.Order)
            {
                listedLater = i;
                continue;
            }

            if (!string.Equals(listed.Meter, added.Meter, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(listed.Area, added.Area, StringComparison.Ordinal))
            {
                return i + 1;
            }

            meterEnd = Math.Max(meterEnd, i + 1);
        }

        return meterEnd < 0 ? listedLater : meterEnd;
    }

    /// <summary>
    /// Forgets an instrument that has ended, or that is no longer listened to.
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
    /// How an instrument's measurements are summed up in its line.
    /// </summary>
    private enum Summary
    {
        /// <summary>
        /// Histograms: how many a second, their mean and most.
        /// </summary>
        Distribution,

        /// <summary>
        /// Counters: the total over the second, and per second.
        /// </summary>
        Total,

        /// <summary>
        /// Observable counters, each measurement the running total: the latest, and how fast it rose.
        /// </summary>
        Running,

        /// <summary>
        /// Gauges: the latest value.
        /// </summary>
        Latest,
    }

    /// <summary>
    /// One instrument's measurements over the second, a series per set of tags. Any thread may record one, so it is
    /// locked. <paramref name="order"/> is where the settings list its meter.
    /// </summary>
    private sealed class Measured(Instrument instrument, int order)
    {
        private readonly Lock _lock = new();
        private readonly List<Series> _series = [];
        private readonly Summary _summary = SummaryOf(instrument);

        /// <summary>
        /// The instrument's name split as its lines show it: its area, the part after its first dot, and its name in the
        /// area, what follows; a name of two parts, or one, is its own area.
        /// </summary>
        private readonly (string Area, string Name) _parts = PartsOf(instrument.Name);

        public string Meter => instrument.Meter.Name;

        public string Area => _parts.Area;

        public int Order => order;

        public void Add(double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            lock (_lock)
            {
                Find(tags).Add(value);
            }
        }

        /// <summary>
        /// Adds a line for each series measured over the last <paramref name="seconds"/> to <paramref name="lines"/>,
        /// after one of the instrument's name when they have tags, and starts each over.
        /// </summary>
        public void Describe(List<TelemetryLine> lines, double seconds)
        {
            lock (_lock)
            {
                bool isNamed = false;
                foreach (Series series in _series)
                {
                    if (series.Count == 0)
                    {
                        continue;
                    }

                    if (series.Tags.Length > 0 && !isNamed)
                    {
                        lines.Add(new TelemetryLine(Meter, Area, _parts.Name, instrument.Description));
                        isNamed = true;
                    }

                    lines.Add(new TelemetryLine(Meter, Area, Describe(series, seconds), instrument.Description));
                    series.Clear();
                }
            }
        }

        private static Summary SummaryOf(Instrument instrument)
        {
            Type kind = instrument.GetType().IsGenericType ? instrument.GetType().GetGenericTypeDefinition() : instrument.GetType();
            if (kind == typeof(Histogram<>))
            {
                return Summary.Distribution;
            }

            if (kind == typeof(Counter<>) || kind == typeof(UpDownCounter<>))
            {
                return Summary.Total;
            }

            return kind == typeof(ObservableCounter<>) || kind == typeof(ObservableUpDownCounter<>) ? Summary.Running : Summary.Latest;
        }

        private static (string Area, string Name) PartsOf(string name)
        {
            int first = name.IndexOf('.', StringComparison.Ordinal);
            int second = first < 0 ? -1 : name.IndexOf('.', first + 1);
            if (second < 0)
            {
                string area = name[(first + 1)..];
                return (area, area);
            }

            return (name[(first + 1)..second], name[(second + 1)..]);
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

        /// <summary>
        /// <paramref name="series"/>' line: the instrument's name when it has no tags, else its tags' values, indented
        /// under the name, then what it measured over the last <paramref name="seconds"/>.
        /// </summary>
        private string Describe(Series series, double seconds)
        {
            string? unit = instrument.Unit;
            ReadOnlySpan<KeyValuePair<string, object?>> tags = series.Tags;
            StringBuilder text = new(tags.Length == 0 ? _parts.Name : "  ");
            for (int i = 0; i < tags.Length; i++)
            {
                text.Append(i == 0 ? string.Empty : " ").Append(tags[i].Value);
            }

            text.Append(": ");
            switch (_summary)
            {
                case Summary.Distribution:
                    text.Append(CultureInfo.InvariantCulture, $"{series.Count / seconds:0.#}/s, mean {Amount(series.Sum / series.Count, unit)}, max {Amount(series.Max, unit)}");
                    break;
                case Summary.Total:
                    text.Append(CultureInfo.InvariantCulture, $"{Amount(series.Sum, unit)}, {Amount(series.Sum / seconds, unit)}/s");
                    break;
                case Summary.Running:
                    text.Append(Amount(series.Last, unit));
                    if (series.Previous is { } previous)
                    {
                        text.Append(", ").Append(Amount((series.Last - previous) / seconds, unit)).Append("/s");
                    }

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
    /// What one instrument measured with one set of tags over the second.
    /// </summary>
    private sealed class Series(KeyValuePair<string, object?>[] tags)
    {
        public ReadOnlySpan<KeyValuePair<string, object?>> Tags => tags;

        public long Count { get; private set; }

        public double Sum { get; private set; }

        public double Max { get; private set; }

        public double Last { get; private set; }

        /// <summary>
        /// The last value of the second before, for a running total's rate; none in the first.
        /// </summary>
        public double? Previous { get; private set; }

        public void Add(double value)
        {
            Max = Count == 0 ? value : Math.Max(Max, value);
            Last = value;
            Sum += value;
            Count++;
        }

        public void Clear()
        {
            Previous = Last;
            Count = 0;
            Sum = 0;
        }

        /// <summary>
        /// Whether <paramref name="other"/> are this series' tags, in the same order: an instrument is recorded with its
        /// tags in one order.
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
}

/// <summary>
/// One line of the telemetry: an instrument's numbers over the last second, its name above a line per set of tags, or
/// one of those, under its meter and its area. <see cref="Description"/> is the instrument's own: what it measures.
/// </summary>
internal readonly record struct TelemetryLine(string Meter, string Area, string Text, string? Description);
