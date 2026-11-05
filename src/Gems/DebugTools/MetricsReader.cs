using System.Diagnostics.CodeAnalysis;
using System.Linq;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace Magic.DebugTools;

/// <summary>
/// How a row sums up its instrument's measurements.
/// </summary>
internal enum MetricKind : byte
{
    /// <summary>
    /// A histogram: its mean, how many a second, and its most.
    /// </summary>
    Distribution,

    /// <summary>
    /// A counter: how much it rose since the last collection, and how much a second.
    /// </summary>
    Total,

    /// <summary>
    /// A gauge or an up-down counter: its latest value.
    /// </summary>
    Latest,
}

/// <summary>
/// One instrument's numbers since the last collection, under one set of its tags: its meter, its name and unit, its tags,
/// and what <see cref="Kind"/> says it sums up as. <see cref="PerSecond"/> and <see cref="Max"/> are 0 where the kind has
/// none. <see cref="MeterTags"/> are the meter's own, what it says of itself, such as the API and the GPU.
/// </summary>
internal readonly record struct MetricRow(
    string Meter,
    string Instrument,
    IReadOnlyList<KeyValuePair<string, string>> Tags,
    string Unit,
    MetricKind Kind,
    double Value,
    double PerSecond,
    double Max,
    IReadOnlyList<KeyValuePair<string, string>> MeterTags
)
{
    /// <summary>
    /// The tags' values, in order, between spaces.
    /// </summary>
    public string TagText => string.Join(' ', Tags.Select(tag => tag.Value));

    /// <summary>
    /// The value of the tag named <paramref name="key"/>, or empty.
    /// </summary>
    public string GetTag(string key)
    {
        foreach ((string name, string value) in Tags)
        {
            if (name == key)
                return value;
        }

        return string.Empty;
    }
}

/// <summary>
/// What every <c>Magic.*</c> meter and .NET's <c>System.Runtime</c> measure, through OpenTelemetry: it sums
/// measurements up on the thread that records them, without locks or allocation, and hands them over as rows when
/// collected, on the main thread. It listens only between <see cref="Listen"/> on and off.
/// </summary>
internal sealed class MetricsReader : IDisposable
{
    private static readonly string[] _meters = ["Magic.*", "System.Runtime"];

    /// <summary>
    /// Where the exporter puts each collection's rows, which the provider's exporter outlives no longer than the provider.
    /// </summary>
    private readonly Collected _collected = new();

    private MeterProvider? _provider;

    /// <summary>
    /// The rows of the last collection, in the order the instruments were published.
    /// </summary>
    public IReadOnlyList<MetricRow> Rows => _collected.Rows;

    public void Dispose() => Listen(false);

    /// <summary>
    /// Starts or stops listening. Stopped, it forgets what it measured.
    /// </summary>
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The provider owns the reader, which owns the exporter; disposing the provider disposes them."
    )]
    public void Listen(bool isListening)
    {
        if (isListening == (_provider is not null))
            return;

        if (!isListening)
        {
            _provider?.Dispose();
            _provider = null;
            _collected.Rows = [];

            return;
        }

        _provider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(_meters)
            .AddReader(
                new BaseExportingMetricReader(new RowExporter(_collected))
                {
                    TemporalityPreference = MetricReaderTemporalityPreference.Delta,
                }
            )
            .Build();
    }

    /// <summary>
    /// Sums up what was measured since the last collection into <see cref="Rows"/>, reading the observable instruments
    /// now. Main thread.
    /// </summary>
    public void Collect() => _provider?.ForceFlush();

    private static MetricKind GetKind(Metric metric) =>
        metric.MetricType switch
        {
            MetricType.Histogram or MetricType.ExponentialHistogram => MetricKind.Distribution,
            MetricType.LongSum or MetricType.DoubleSum when metric.Temporality == AggregationTemporality.Delta =>
                MetricKind.Total,
            MetricType.LongSum
            or MetricType.DoubleSum
            or MetricType.LongSumNonMonotonic
            or MetricType.DoubleSumNonMonotonic
            or MetricType.LongGauge
            or MetricType.DoubleGauge => MetricKind.Latest,
            _ => MetricKind.Latest,
        };

    private static double ReadSum(MetricType type, in MetricPoint point) =>
        type.IsLong() ? point.GetSumLong() : point.GetSumDouble();

    private static double ReadLatest(MetricType type, in MetricPoint point)
    {
        if (!type.IsGauge())
            return ReadSum(type, point);

        return type.IsLong() ? point.GetGaugeLastValueLong() : point.GetGaugeLastValueDouble();
    }

    /// <summary>
    /// <paramref name="point"/>'s row of <paramref name="metric"/>.
    /// </summary>
    private static MetricRow ToRow(
        Metric metric,
        in MetricPoint point,
        IReadOnlyList<KeyValuePair<string, string>> meterTags
    )
    {
        List<KeyValuePair<string, string>> tags = [];
        foreach (KeyValuePair<string, object?> tag in point.Tags)
        {
            tags.Add(new KeyValuePair<string, string>(tag.Key, tag.Value?.ToString() ?? string.Empty));
        }

        double windowSec = Math.Max((point.EndTime - point.StartTime).TotalSeconds, 1e-6);
        MetricKind kind = GetKind(metric);
        (double value, double perSecond, double max) = kind switch
        {
            MetricKind.Distribution => (
                point.GetHistogramCount() > 0 ? point.GetHistogramSum() / point.GetHistogramCount() : 0,
                point.GetHistogramCount() / windowSec,
                point.TryGetHistogramMinMaxValues(out _, out double most) ? most : 0
            ),
            MetricKind.Total => (ReadSum(metric.MetricType, point), ReadSum(metric.MetricType, point) / windowSec, 0),
            MetricKind.Latest => (ReadLatest(metric.MetricType, point), 0, 0),
            _ => (ReadLatest(metric.MetricType, point), 0, 0),
        };

        return new MetricRow(metric.MeterName, metric.Name, tags, metric.Unit, kind, value, perSecond, max, meterTags);
    }

    /// <summary>
    /// The last collection's rows, which the exporter writes and the reader reads.
    /// </summary>
    private sealed class Collected
    {
        public IReadOnlyList<MetricRow> Rows { get; set; } = [];
    }

    /// <summary>
    /// Makes the rows of each batch the reader collects, as it is handed it, on the thread that collects it.
    /// </summary>
    private sealed class RowExporter(Collected collected) : BaseExporter<Metric>
    {
        public override ExportResult Export(in Batch<Metric> batch)
        {
            List<MetricRow> rows = [];
            foreach (Metric metric in batch)
            {
                List<KeyValuePair<string, string>> meterTags = [];
                foreach (KeyValuePair<string, object?> tag in metric.MeterTags ?? [])
                {
                    meterTags.Add(new KeyValuePair<string, string>(tag.Key, tag.Value?.ToString() ?? string.Empty));
                }

                foreach (ref readonly MetricPoint point in metric.GetMetricPoints())
                {
                    rows.Add(ToRow(metric, point, meterTags));
                }
            }

            collected.Rows = rows;

            return ExportResult.Success;
        }
    }
}
