using Magic.Contexts;
using Magic.Interfaces;
using Magic.Services;
using Magic.Utils;
using System.Globalization;
using System.Text;

namespace DebugToolsGem;

/// <summary>
/// The engine's numbers about itself, set every frame whether anything shows them: frames per second and frame time
/// (<c>Frame.*</c>) and the GC (<c>Memory.GC.*</c>); and every stat in <see cref="Telemetry"/> logged (verbose, so only
/// with --verbose) every <see cref="LogIntervalMs"/>, so a headless or scripted run can have them too.
/// </summary>
internal sealed class FrameStats(Telemetry telemetry) : ISystem
{
    public const double LogIntervalMs = 30000;

    private readonly List<(string Name, double Value)> _stats = [];
    private readonly StringBuilder _line = new();
    private readonly int[] _sampledCollections = new int[3];

    /// <summary>
    /// The frame time, smoothed over the last frames.
    /// </summary>
    private double FrameMs { get; set; }

    /// <summary>
    /// The last frame's time, as <c>Frame.WorstMs</c> shows it.
    /// </summary>
    private double LastFrameMs { get; set; }

    private double SinceLogMs { get; set; }

    private double SinceGcSampleMs { get; set; }

    private long SampledAllocatedBytes { get; set; }

    private TimeSpan SampledPause { get; set; }

    /// <summary>
    /// Whole numbers as they are, anything else to two places.
    /// </summary>
    public static string Format(double value)
    {
        return value == Math.Floor(value) && Math.Abs(value) < 1e15
            ? value.ToString("0", CultureInfo.InvariantCulture)
            : value.ToString("0.00", CultureInfo.InvariantCulture);
    }

    public void Run(in Frame frame)
    {
        double dtMs = frame.Delta * 1000d;
        FrameMs = FrameMs == 0 ? dtMs : FrameMs + ((dtMs - FrameMs) * 0.05);
        double fps = FrameMs > 0 ? 1000 / FrameMs : 0;
        telemetry.Set("Frame.Ms", FrameMs);
        telemetry.Set("Frame.Fps", fps);
        telemetry.Set("Frame.WorstMs", LastFrameMs);
        LastFrameMs = dtMs;
        SampleGc(dtMs);

        SinceLogMs += dtMs;
        if (SinceLogMs < LogIntervalMs)
            return;

        SinceLogMs = 0;
        _stats.Clear();
        telemetry.CopyTo(_stats);
        _line.Clear();
        foreach ((string name, double value) in _stats)
            _line.Append(_line.Length == 0 ? "" : ", ").Append(name).Append(' ').Append(Format(value));

        Log.Verbose($"Stats: {_line}.");
        if (fps < 30)
            Log.Verbose("FPS is below 30! Consider profiling and optimizing.");
    }

    /// <summary>
    /// Rates over the last second rather than per frame, so they are readable: allocation in MB/s, collections per
    /// generation per second, and the share of wall time the GC had threads paused.
    /// </summary>
    private void SampleGc(double dtMs)
    {
        SinceGcSampleMs += dtMs;
        if (SinceGcSampleMs < 1000)
            return;

        long allocated = GC.GetTotalAllocatedBytes();
        TimeSpan pause = GC.GetTotalPauseDuration();
        double seconds = SinceGcSampleMs / 1000d;
        telemetry.Set("Memory.GC.HeapMB", Megabytes(GC.GetTotalMemory(false)));
        telemetry.Set("Memory.GC.AllocatedMBPerSecond", Megabytes(allocated - SampledAllocatedBytes) / seconds);
        telemetry.Set("Memory.GC.PausedPercent", (pause - SampledPause).TotalMilliseconds / SinceGcSampleMs * 100);
        for (int generation = 0; generation < _sampledCollections.Length; generation++)
        {
            int count = GC.CollectionCount(generation);
            telemetry.Set($"Memory.GC.Gen{generation}Collections", count);
            telemetry.Set($"Memory.GC.Gen{generation}PerSecond", (count - _sampledCollections[generation]) / seconds);
            _sampledCollections[generation] = count;
        }

        SampledAllocatedBytes = allocated;
        SampledPause = pause;
        SinceGcSampleMs = 0;
    }

    private static double Megabytes(long bytes)
    {
        return bytes / (1024d * 1024d);
    }
}
