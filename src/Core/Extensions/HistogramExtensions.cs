using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Magic.Extensions;

/// <summary>
/// Timing something into a histogram of seconds (<see cref="MeterExtensions"/>), only while something listens.
/// </summary>
public static class HistogramExtensions
{
    extension(Histogram<double> timer)
    {
        /// <summary>
        /// The moment a timing starts, to hand to <see cref="RecordSince"/>: 0 while nothing listens, so nothing is
        /// recorded.
        /// </summary>
        public long Start() => timer.Enabled ? Stopwatch.GetTimestamp() : 0;

        /// <summary>
        /// Records the seconds since <paramref name="startTimestamp"/>, tagged with <paramref name="tags"/>, unless
        /// nothing listened when it started.
        /// </summary>
        public void RecordSince(long startTimestamp, params ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            if (startTimestamp == 0) return;

            timer.Record(Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds, tags);
        }
    }
}
