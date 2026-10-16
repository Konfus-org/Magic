using System.Diagnostics.Metrics;

namespace Magic.Internal;

/// <summary>
/// Core's meter, <c>Magic.Core</c>, which its parts record on (Core's README, Telemetry), and the bucket boundaries its
/// histograms of time advise, as exporters' own are made for milliseconds and would put everything in the first.
/// </summary>
internal static class Metrics
{
    public static Meter Meter { get; } = new("Magic.Core");

    /// <summary>
    /// For what happens within a frame: a tenth of a millisecond to a second, with edges at the frame budgets of 240,
    /// 144, 120, 60 and 30 frames a second.
    /// </summary>
    public static InstrumentAdvice<double> FrameBuckets { get; } = new()
    {
        HistogramBucketBoundaries = [0.0001, 0.00025, 0.0005, 0.001, 0.002, 1.0 / 240, 1.0 / 144, 1.0 / 120, 1.0 / 60, 1.0 / 30, 0.05, 0.1, 0.25, 0.5, 1],
    };

    /// <summary>
    /// For work that spans frames, such as a load: a millisecond to ten seconds.
    /// </summary>
    public static InstrumentAdvice<double> WorkBuckets { get; } = new()
    {
        HistogramBucketBoundaries = [0.001, 0.0025, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10],
    };
}
