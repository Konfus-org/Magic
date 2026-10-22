using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace Magic.DebugTools;

/// <summary>
/// One line of the telemetry: an instrument's numbers over the last second, its name above a line per set of tags, or
/// one of those, under its tab and its header, as the cells of a row: its name, its value (a histogram's mean, a
/// counter's total, a gauge's latest), how many or how much a second, and a histogram's most, each empty where the
/// instrument has none. <see cref="Description"/> is the instrument's own: what it measures.
/// </summary>
internal readonly record struct TelemetryLine(
    string Tab,
    string Header,
    string Name,
    string Value,
    string Rate,
    string Max,
    string? Description)
{
    /// <summary>
    /// The row as one line of text, for Copy: the name, then the cells it has.
    /// </summary>
    internal string Text
    {
        get
        {
            StringBuilder text = new(Name);
            string between = ": ";
            foreach (string cell in (ReadOnlySpan<string>)[Value, Rate, Max.Length == 0 ? string.Empty : $"max {Max}"])
            {
                if (cell.Length > 0)
                {
                    text.Append(between).Append(cell);
                    between = ", ";
                }
            }

            return text.ToString();
        }
    }
}

/// <summary>
/// What the meters listened to measure, summed up each second as rows laid out as the instruments are named, as an
/// exporter shows them: a tab per entry of the settings' tabs, holding the meters under its prefix, a meter's rows
/// together and, in one, an area's. An area is the part of an instrument's name after its meter's, so
/// <c>magic.core.frame.duration</c> is <c>duration</c> in the area <c>frame</c> of <c>Magic.Core</c>, and a name that
/// does not start with its meter's, as .NET's <c>dotnet.gc.collections</c>, loses its first part instead; a name with
/// nothing after its area is its own area. An instrument measured without tags is one row; with tags, a row of its
/// name, then one per set of tags, showing their values. A row's cells are the name, the value, how many or how much a
/// second, and the most: a histogram's are its mean, how many a second and its most, so the frame's duration says the
/// frame time, the frame rate and the slowest frame; a counter's its total over the second and its rate; a running
/// total's, such as the garbage collector's, the total and how fast it rose; a gauge's its latest value alone. Seconds
/// are shown as milliseconds, bytes as megabytes, and a count's unit, such as <c>{collection}</c>, not at all, as the
/// name says what is counted.
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
    /// The instruments listened to, kept in the order their lines show: the tabs as the settings list them, a meter's
    /// together and, in one, an area's, each in the order they were first published.
    /// </summary>
    private readonly List<Measured> _measured = [];

    private readonly List<TelemetryLine> _lines = [];
    private string[] _tabs = [];
    private MeterListener? _listener;

    /// <summary>
    /// The tabs listened under, so a change to them while running listens again.
    /// </summary>
    private IReadOnlyDictionary<string, string> _tabsListened = settings.Tabs;

    /// <summary>
    /// When the second being summed up began.
    /// </summary>
    private long _secondStart;

    /// <summary>
    /// The last second's lines, in the order they show.
    /// </summary>
    internal IReadOnlyList<TelemetryLine> Lines => _lines;

    /// <summary>
    /// The tabs of the last second's lines, in their order.
    /// </summary>
    internal ReadOnlySpan<string> Tabs => _tabs;

    public void Dispose()
    {
        _listener?.Dispose();
        _listener = null;
    }

    /// <summary>
    /// Starts or stops listening. Stopped, it forgets what it measured.
    /// </summary>
    internal void Listen(bool listening)
    {
        if (listening == (_listener is not null)) return;

        if (_listener is { } listener)
        {
            listener.Dispose();
            _listener = null;
            _lines.Clear();
            _tabs = [];

            return;
        }

        _tabsListened = settings.Tabs;
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
    internal void Refresh()
    {
        // A change to the settings' tabs hands a new list, and listening starts again under it.
        if (_listener is not null && !ReferenceEquals(_tabsListened, settings.Tabs))
        {
            Listen(false);
            Listen(true);
        }

        double seconds = Stopwatch.GetElapsedTime(_secondStart).TotalSeconds;

        if (_listener is not { } listener || seconds < 1) return;

        // The second starts over, and the observable instruments are read now, so the lines have their latest values.
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

        _tabs = GetTabs(_lines);
    }

    /// <summary>
    /// Every line as one text, under its tab's name and its header's.
    /// </summary>
    internal string GetAllText()
    {
        StringBuilder text = new();
        string tab = string.Empty;
        string header = string.Empty;
        foreach (TelemetryLine line in _lines)
        {
            if (!string.Equals(line.Tab, tab, StringComparison.Ordinal))
            {
                tab = line.Tab;
                header = string.Empty;
                text.Append(text.Length == 0 ? string.Empty : "\n").Append("# ").Append(tab).Append('\n');
            }

            if (!string.Equals(line.Header, header, StringComparison.Ordinal))
            {
                header = line.Header;
                text.Append(header).Append('\n');
            }

            text.Append("  ").Append(line.Text).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>
    /// Whether <paramref name="meter"/> is <paramref name="prefix"/> or under it: "Magic" takes "Magic.Core" and
    /// "Magic.Gems.&lt;Gem&gt;", not "Magical".
    /// </summary>
    private static bool IsUnder(string meter, string prefix)
    {
        return meter.StartsWith(prefix, StringComparison.Ordinal)
            && (meter.Length == prefix.Length || meter[prefix.Length] == '.');
    }

    /// <summary>
    /// The tabs of <paramref name="lines"/>, which keep each tab's together, in their order.
    /// </summary>
    private static string[] GetTabs(List<TelemetryLine> lines)
    {
        List<string> tabs = [];
        foreach (TelemetryLine line in lines)
        {
            if (tabs.Count == 0 || !string.Equals(tabs[^1], line.Tab, StringComparison.Ordinal)) tabs.Add(line.Tab);
        }

        return [.. tabs];
    }

    /// <summary>
    /// <paramref name="value"/> in <paramref name="unit"/>, with seconds shown as milliseconds, as most of what is
    /// timed in a frame takes far less than one, bytes as megabytes, and a count's unit, in braces, left out.
    /// </summary>
    private static string FormatAmount(double value, string? unit) => unit switch
    {
        "s" => string.Create(CultureInfo.InvariantCulture, $"{value * 1000:0.###} ms"),
        "By" => string.Create(CultureInfo.InvariantCulture, $"{value / (1024 * 1024):0.#} MB"),
        null or "" or ['{', ..] => value.ToString("0.###", CultureInfo.InvariantCulture),
        _ => string.Create(CultureInfo.InvariantCulture, $"{value:0.###} {unit}"),
    };

    /// <summary>
    /// One measurement into its instrument's series for its tags, on the thread that recorded it.
    /// </summary>
    private static void Add<T>(
        Instrument instrument,
        T measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        object? state)
        where T : struct, INumber<T>
    {
        if (state is Measured measured) measured.Add(double.CreateTruncating(measurement), tags);
    }

    /// <summary>
    /// Listens to a new instrument when its meter is under one of the settings' tabs, as they are when listening
    /// starts.
    /// </summary>
    private void Publish(Instrument instrument, MeterListener listener)
    {
        string meter = instrument.Meter.Name;
        int order = 0;
        foreach ((string tab, string prefix) in _tabsListened)
        {
            if (IsUnder(meter, prefix))
            {
                Measured measured = new(instrument, order, tab, prefix);
                lock (_lock)
                {
                    _measured.Insert(PlaceOf(measured), measured);
                }

                listener.EnableMeasurementEvents(instrument, measured);

                return;
            }

            order++;
        }
    }

    /// <summary>
    /// Where <paramref name="added"/> goes among the instruments listened to, to keep their order: after the last of
    /// its meter and area, else after the last of its meter, else before the first whose tab the settings list later.
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

            if (!string.Equals(listed.Meter, added.Meter, StringComparison.Ordinal)) continue;

            if (string.Equals(listed.Area, added.Area, StringComparison.Ordinal)) return i + 1;

            meterEnd = Math.Max(meterEnd, i + 1);
        }

        return meterEnd < 0 ? listedLater : meterEnd;
    }

    /// <summary>
    /// Forgets an instrument that has ended, or that is no longer listened to.
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
    /// locked. <paramref name="order"/> is where the settings list its tab, <paramref name="tab"/>, and
    /// <paramref name="prefix"/> the tab's, which its meter is under.
    /// </summary>
    private sealed class Measured(Instrument instrument, int order, string tab, string prefix)
    {
        private readonly Lock _lock = new();
        private readonly List<Series> _series = [];
        private readonly Summary _summary = GetSummary(instrument);

        /// <summary>
        /// The instrument's name split as its lines show it: its area, its name in the area, and the header its lines
        /// are under, its meter's name past the tab's prefix and then its area, or the area alone for the meter the
        /// prefix names.
        /// </summary>
        private readonly (string Area, string Name, string Header) _parts = PartsOf(instrument, prefix);

        internal string Tab => tab;

        internal string Meter => instrument.Meter.Name;

        internal string Area => _parts.Area;

        internal int Order => order;

        internal void Add(double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
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
        internal void Describe(List<TelemetryLine> lines, double seconds)
        {
            lock (_lock)
            {
                bool isNamed = false;
                foreach (Series series in _series)
                {
                    if (series.Count == 0) continue;

                    if (series.Tags.Length > 0 && !isNamed)
                    {
                        lines.Add(new TelemetryLine(
                            Tab,
                            _parts.Header,
                            _parts.Name,
                            string.Empty,
                            string.Empty,
                            string.Empty,
                            instrument.Description));
                        isNamed = true;
                    }

                    lines.Add(Describe(series, seconds));
                    series.Clear();
                }
            }
        }

        private static Summary GetSummary(Instrument instrument)
        {
            Type type = instrument.GetType();
            Type kind = type.IsGenericType ? type.GetGenericTypeDefinition() : type;

            if (kind == typeof(Histogram<>)) return Summary.Distribution;

            if (kind == typeof(Counter<>) || kind == typeof(UpDownCounter<>)) return Summary.Total;

            return kind == typeof(ObservableCounter<>) || kind == typeof(ObservableUpDownCounter<>)
                ? Summary.Running
                : Summary.Latest;
        }

        /// <summary>
        /// <paramref name="instrument"/>'s name without its meter's: what follows the meter's name in snake_case, as
        /// instruments are named after their meter (<c>magic.core.</c> for <c>Magic.Core</c>), or what follows its
        /// first part when it is named otherwise; then its area, up to the next dot, and its name in the area, the
        /// rest, or the area again when there is none. The header is the meter past the tab's <paramref name="prefix"/>
        /// and the area, or the area alone for the meter the prefix names.
        /// </summary>
        private static (string Area, string Name, string Header) PartsOf(Instrument instrument, string prefix)
        {
            string meter = instrument.Meter.Name;
            string name = instrument.Name;
            string[] parts = Array.ConvertAll(meter.Split('.'), JsonNamingPolicy.SnakeCaseLower.ConvertName);
            string own = string.Join('.', parts) + ".";
            string rest = name.StartsWith(own, StringComparison.Ordinal)
                ? name[own.Length..]
                : name[(name.IndexOf('.', StringComparison.Ordinal) + 1)..];
            int dot = rest.IndexOf('.', StringComparison.Ordinal);
            string area = dot < 0 ? rest : rest[..dot];

            return (
                area,
                dot < 0 ? rest : rest[(dot + 1)..],
                meter.Length == prefix.Length ? area : $"{meter[(prefix.Length + 1)..]} {area}");
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

        /// <summary>
        /// <paramref name="series"/>' row: the instrument's name when it has no tags, else its tags' values, indented
        /// under the name, then what it measured over the last <paramref name="seconds"/>, in the cells it has.
        /// </summary>
        private TelemetryLine Describe(Series series, double seconds)
        {
            string? unit = instrument.Unit;
            ReadOnlySpan<KeyValuePair<string, object?>> tags = series.Tags;
            StringBuilder name = new(tags.Length == 0 ? _parts.Name : "  ");
            for (int i = 0; i < tags.Length; i++)
            {
                name.Append(i == 0 ? string.Empty : " ").Append(tags[i].Value);
            }

            (string value, string rate, string max) = _summary switch
            {
                Summary.Distribution => (
                    FormatAmount(series.Sum / series.Count, unit),
                    string.Create(CultureInfo.InvariantCulture, $"{series.Count / seconds:0.#}/s"),
                    FormatAmount(series.Max, unit)),
                Summary.Total => (
                    FormatAmount(series.Sum, unit),
                    $"{FormatAmount(series.Sum / seconds, unit)}/s",
                    string.Empty),
                Summary.Running => (
                    FormatAmount(series.Last, unit),
                    series.Previous is { } previous
                        ? $"{FormatAmount((series.Last - previous) / seconds, unit)}/s"
                        : string.Empty,
                    string.Empty),
                Summary.Latest or _ => (FormatAmount(series.Last, unit), string.Empty, string.Empty),
            };

            return new TelemetryLine(Tab, _parts.Header, name.ToString(), value, rate, max, instrument.Description);
        }
    }

    /// <summary>
    /// What one instrument measured with one set of tags over the second.
    /// </summary>
    private sealed class Series(KeyValuePair<string, object?>[] tags)
    {
        internal ReadOnlySpan<KeyValuePair<string, object?>> Tags => tags;

        internal long Count { get; private set; }

        internal double Sum { get; private set; }

        internal double Max { get; private set; }

        internal double Last { get; private set; }

        /// <summary>
        /// The last value of the second before, for a running total's rate; none in the first.
        /// </summary>
        internal double? Previous { get; private set; }

        internal void Add(double value)
        {
            Max = Count == 0 ? value : Math.Max(Max, value);
            Last = value;
            Sum += value;
            Count++;
        }

        internal void Clear()
        {
            // The most starts over from the next value, which comes with a count of 0.
            Previous = Last;
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
}
