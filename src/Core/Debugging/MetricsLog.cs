using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using Magic.Threading;

namespace Magic.Debugging;

/// <summary>
/// Sums up what the <c>Magic.*</c> meters and .NET's <c>System.Runtime</c> measure over a time, one time or at an
/// interval.
/// </summary>
/// <remarks>
/// <para>
/// It takes each instrument with the category as a part of its name, such as gpu in
/// <c>magic.gems.&lt;gem&gt;.gpu.pass.duration</c>, or each instrument for <c>all</c>. It writes a line for each set
/// of tags.
/// </para>
/// <para>
/// A histogram shows its mean, its count a second and its maximum. A counter shows its rise and its rise a second.
/// Other instruments show their latest value. Seconds show as milliseconds and bytes as megabytes. It listens only
/// while it measures.
/// </para>
/// </remarks>
internal static class MetricsLog
{
    public const string All = "all";

    /// <summary>
    /// The category of the load instruments, such as <c>magic.core.assets.load.duration</c>. Unlike others, it measures
    /// from the start of the process, because a load is over before a command can ask for it.
    /// </summary>
    public const string Load = "load";

    /// <summary>
    /// Records the <see cref="Load"/> instruments from the start of the process; null before that.
    /// </summary>
    private static Sampler? _loads;

    /// <summary>
    /// Stops the running log; null while none runs.
    /// </summary>
    private static CancellationTokenSource? _stop;

    /// <summary>
    /// Starts to record the <see cref="Load"/> instruments until the result is disposed.
    /// </summary>
    public static IDisposable RecordLoads()
    {
        _loads = new Sampler(Load);

        return _loads;
    }

    /// <summary>
    /// Logs <paramref name="category"/> every <paramref name="everySec"/> seconds, in place of the log before. 0 stops
    /// it.
    /// </summary>
    public static void Start(string category, double everySec)
    {
        _stop?.Cancel();
        _stop?.Dispose();
        _stop = everySec > 0 ? new CancellationTokenSource() : null;
        if (_stop is { } stop)
            LogAsync(category, TimeSpan.FromSeconds(everySec), stop.Token).FireAndForget();
    }

    /// <summary>
    /// Measures <paramref name="category"/> over the next <paramref name="over"/>, and gives the lines a log prints.
    /// </summary>
    public static async Task<string> SampleAsync(string category, TimeSpan over, CancellationToken cancel)
    {
        // What the loads of the run so far measured, which a print does not clear.
        if (category == Load && _loads is { } loads)
            return loads.Describe();

        using Sampler sampler = new(category);
        await Task.Delay(over, cancel).ConfigureAwait(false);

        return sampler.Summarize();
    }

    /// <summary>
    /// Runs one log until it is cancelled: each tick sums up and logs what came since the last tick.
    /// </summary>
    private static async Task LogAsync(string category, TimeSpan every, CancellationToken cancel)
    {
        using Sampler sampler = new(category);
        using PeriodicTimer timer = new(every);
        while (await timer.WaitForNextTickAsync(cancel).ConfigureAwait(false))
        {
            Log.Info(sampler.Summarize());
        }
    }

    private static string DescribeSum(string name, in Sum sum, double seconds)
    {
        string unit = sum.Instrument.Unit ?? string.Empty;
        string perSecond = string.Create(CultureInfo.InvariantCulture, $"{sum.Count / seconds:0.#}/s");

        return sum.Instrument switch
        {
            Histogram<double> or Histogram<float> or Histogram<long> or Histogram<int> =>
                $"{name}: {Format(sum.Total / sum.Count, unit)}, {perSecond}, max {Format(sum.Max, unit)}",
            Counter<double> or Counter<float> or Counter<long> or Counter<int> =>
                $"{name}: {Format(sum.Total, unit)}, {Format(sum.Total / seconds, unit)}/s",
            _ => $"{name}: {Format(sum.Latest, unit)}",
        };
    }

    /// <summary>
    /// Formats <paramref name="value"/> in <paramref name="unit"/>: seconds as milliseconds, bytes as megabytes, and a
    /// count without its unit in braces.
    /// </summary>
    private static string Format(double value, string unit) =>
        unit switch
        {
            "s" => string.Create(CultureInfo.InvariantCulture, $"{value * 1000:0.###} ms"),
            "By" => string.Create(CultureInfo.InvariantCulture, $"{value / (1024 * 1024):0.#} MB"),
            "" or ['{', ..] => value.ToString("0.###", CultureInfo.InvariantCulture),
            _ => string.Create(CultureInfo.InvariantCulture, $"{value:0.###} {unit}"),
        };

    /// <summary>
    /// What one instrument measured under one set of tags since the last summary.
    /// </summary>
    private readonly record struct Sum(Instrument Instrument, int Count, double Total, double Max, double Latest);

    /// <summary>
    /// Listens to one category's instruments until it is disposed, and sums up what came since the last summary.
    /// </summary>
    /// <remarks>
    /// The threads that record add each measurement to its sum under a lock, so the memory stays flat however long it
    /// listens.
    /// </remarks>
    private sealed class Sampler : IDisposable
    {
        private readonly string _category;
        private readonly MeterListener _listener;
        private readonly Lock _lock = new();
        private Dictionary<string, Sum> _sums = [];
        private long _sinceTimestamp = Stopwatch.GetTimestamp();

        public Sampler(string category)
        {
            _category = category;
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, listening) =>
                {
                    // The Magic.* meters and .NET's runtime, each instrument with the category as a part of its name.
                    string meter = instrument.Meter.Name;
                    bool isOurs = meter.StartsWith("Magic.", StringComparison.Ordinal) || meter == "System.Runtime";
                    if (isOurs && (category == All || instrument.Name.Split('.').Contains(category)))
                        listening.EnableMeasurementEvents(instrument);
                },
            };
            _listener.SetMeasurementEventCallback<double>(Record);
            _listener.SetMeasurementEventCallback<float>(Record);
            _listener.SetMeasurementEventCallback<long>(Record);
            _listener.SetMeasurementEventCallback<int>(Record);
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();

        /// <summary>
        /// Describes what was measured since the last summary or the start: a line per instrument and set of tags. It
        /// clears nothing.
        /// </summary>
        public string Describe()
        {
            _listener.RecordObservableInstruments();
            Dictionary<string, Sum> sums;
            double seconds;
            lock (_lock)
            {
                sums = new(_sums);
                seconds = Math.Max(Stopwatch.GetElapsedTime(_sinceTimestamp).TotalSeconds, 1e-6);
            }

            return Format(sums, seconds);
        }

        /// <summary>
        /// Describes what was measured since the last summary or the start, then starts from nothing. A measurement
        /// is in this summary or in the next one, never lost.
        /// </summary>
        public string Summarize()
        {
            _listener.RecordObservableInstruments();
            Dictionary<string, Sum> sums;
            double seconds;
            lock (_lock)
            {
                sums = _sums;
                _sums = [];
                seconds = Math.Max(Stopwatch.GetElapsedTime(_sinceTimestamp).TotalSeconds, 1e-6);
                _sinceTimestamp = Stopwatch.GetTimestamp();
            }

            return Format(sums, seconds);
        }

        private string Format(Dictionary<string, Sum> sums, double seconds)
        {
            string described = string.Join(
                '\n',
                sums.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => DescribeSum(pair.Key, pair.Value, seconds))
            );

            return string.Create(
                CultureInfo.InvariantCulture,
                $"Stats, {_category}, over {seconds:0.#} s:\n{described}"
            );
        }

        private void Record<T>(
            Instrument instrument,
            T value,
            ReadOnlySpan<KeyValuePair<string, object?>> tags,
            object? state
        )
            where T : struct, INumberBase<T>
        {
            string key = instrument.Name;
            if (!tags.IsEmpty)
            {
                DefaultInterpolatedStringHandler text = new(0, 0, CultureInfo.InvariantCulture, stackalloc char[256]);
                text.AppendLiteral(key);
                foreach (KeyValuePair<string, object?> tag in tags)
                {
                    text.AppendLiteral(" ");
                    text.AppendFormatted(tag.Value);
                }

                key = text.ToStringAndClear();
            }

            double measured = double.CreateTruncating(value);
            lock (_lock)
            {
                Sum sum = _sums.GetValueOrDefault(key, new Sum(instrument, 0, 0, double.MinValue, 0));
                _sums[key] = sum with
                {
                    Count = sum.Count + 1,
                    Total = sum.Total + measured,
                    Max = Math.Max(sum.Max, measured),
                    Latest = measured,
                };
            }
        }
    }
}
