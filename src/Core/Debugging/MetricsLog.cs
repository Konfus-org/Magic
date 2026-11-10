using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading.Channels;
using Magic.Threading;

namespace Magic.Debugging;

/// <summary>
/// Sums up what the <c>Magic.*</c> meters and .NET's <c>System.Runtime</c> measure over a stretch of time, once or
/// logged every so many seconds: each instrument with the category as a part of its name, such as gpu in
/// <c>magic.gems.vulkan_graphics.gpu.pass.duration</c>, or every one for all, a line for each of its sets of tags. A
/// histogram shows its mean, how many a second and its most, a counter how much it rose and how much a second, and
/// anything else its latest value; seconds show as milliseconds and bytes as megabytes. It listens only while it
/// measures.
/// </summary>
internal static class MetricsLog
{
    public const string All = "all";

    /// <summary>
    /// Ends the log that runs, while one does.
    /// </summary>
    private static CancellationTokenSource? _stop;

    /// <summary>
    /// Logs <paramref name="category"/> every <paramref name="everySec"/> seconds from now, in place of what it logged
    /// before; 0 stops it.
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
    /// What <paramref name="category"/> measures over the next <paramref name="over"/>, as the lines a log prints.
    /// </summary>
    public static async Task<string> SampleAsync(string category, TimeSpan over, CancellationToken cancel)
    {
        using Sampler sampler = new(category);
        await Task.Delay(over, cancel).ConfigureAwait(false);

        return sampler.Summarize();
    }

    /// <summary>
    /// One log until it is cancelled: each tick sums up what came since the last and logs it.
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

    private static bool IsLogged(Instrument instrument, string category) =>
        (
            instrument.Meter.Name.StartsWith("Magic.", StringComparison.Ordinal)
            || instrument.Meter.Name == "System.Runtime"
        ) && (category == All || instrument.Name.Split('.').Contains(category));

    private static string Describe(string name, in Sum sum, double seconds)
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
    /// <paramref name="value"/> in <paramref name="unit"/>: seconds as milliseconds, bytes as megabytes, and a count's
    /// unit, in braces, left out.
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
    /// One measurement, as the thread that recorded it hands it over: its instrument, its name with its tags' values,
    /// and its value.
    /// </summary>
    private readonly record struct Measured(Instrument Instrument, string Key, double Value);

    /// <summary>
    /// What one instrument measured under one set of tags since the last summary.
    /// </summary>
    private readonly record struct Sum(Instrument Instrument, int Count, double Total, double Max, double Latest);

    /// <summary>
    /// Listens to one category's instruments from when it is made until it is disposed: the threads that record hand
    /// each measurement over through a channel, and each summary sums up what came since the last.
    /// </summary>
    private sealed class Sampler : IDisposable
    {
        private readonly string _category;
        private readonly MeterListener _listener;
        private readonly Channel<Measured> _measured = Channel.CreateUnbounded<Measured>(
            new UnboundedChannelOptions { SingleReader = true }
        );
        private readonly Dictionary<string, Sum> _sums = [];
        private long _sinceTimestamp = Stopwatch.GetTimestamp();

        public Sampler(string category)
        {
            _category = category;
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, listening) =>
                {
                    if (IsLogged(instrument, category))
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
        /// What was measured since the last summary, or since the start, a line per instrument and set of tags.
        /// </summary>
        public string Summarize()
        {
            _listener.RecordObservableInstruments();
            while (_measured.Reader.TryRead(out Measured measurement))
            {
                Sum sum = _sums.GetValueOrDefault(
                    measurement.Key,
                    new Sum(measurement.Instrument, 0, 0, double.MinValue, 0)
                );
                _sums[measurement.Key] = sum with
                {
                    Count = sum.Count + 1,
                    Total = sum.Total + measurement.Value,
                    Max = Math.Max(sum.Max, measurement.Value),
                    Latest = measurement.Value,
                };
            }

            double seconds = Math.Max(Stopwatch.GetElapsedTime(_sinceTimestamp).TotalSeconds, 1e-6);
            _sinceTimestamp = Stopwatch.GetTimestamp();
            string described = string.Join(
                '\n',
                _sums
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => Describe(pair.Key, pair.Value, seconds))
            );
            _sums.Clear();

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
            string tagText = string.Join(' ', tags.ToArray().Select(tag => tag.Value));
            string key = tagText.Length == 0 ? instrument.Name : $"{instrument.Name} {tagText}";
            _measured.Writer.TryWrite(new Measured(instrument, key, double.CreateTruncating(value)));
        }
    }
}
