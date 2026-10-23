using System.Diagnostics.Metrics;
using Magic.Contexts.Telemetry;

namespace Magic.Extensions;

/// <summary>
/// Making a histogram of seconds, timed with <see cref="HistogramExtensions"/>.
/// </summary>
public static class MeterExtensions
{
    extension(Meter meter)
    {
        /// <summary>
        /// A histogram of seconds, advising <paramref name="buckets"/>, such as <see cref="Metrics.FrameBuckets"/>.
        /// </summary>
        public Histogram<double> CreateTimer(string name, string description, InstrumentAdvice<double> buckets)
            => meter.CreateHistogram(name, "s", description, tags: null, advice: buckets);
    }
}
