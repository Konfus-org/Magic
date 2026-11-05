using System.Globalization;
using Magic.Debugging;

namespace Magic.DebugTools;

/// <summary>
/// The frame rate and the memory in use, as a line in the top left corner, shown from the start in a Debug build:
/// <c>FPS: 59 (17.0 ms) | Memory: GPU 0.4 GB, RAM 0.9 GB</c>. The frame's duration comes from Core's meter, the GPU's
/// memory from whatever gem publishes a <c>gpu.memory.usage</c>, and the process's from .NET's meter.
/// </summary>
internal sealed class CounterLine(IDebugUI ui)
{
    private const string FrameDuration = "magic.core.frame.duration";
    private const string GpuMemorySuffix = ".gpu.memory.usage";
    private const string ProcessMemory = "dotnet.process.memory.working_set";
    private const double BytesPerGigabyte = 1024 * 1024 * 1024;

#if DEBUG
    private const bool IsShownAtStart = true;
#else
    private const bool IsShownAtStart = false;
#endif

    private string _line = string.Empty;

    public bool IsShown
    {
        get;
        set
        {
            field = value;
            _line = string.Empty;
        }
    } = IsShownAtStart;

    /// <summary>
    /// The line written out again from the last collection's <paramref name="rows"/>.
    /// </summary>
    public void Refresh(IReadOnlyList<MetricRow> rows)
    {
        double framesPerSecond = 0;
        double frameMs = 0;
        double gpuBytes = 0;
        double processBytes = 0;
        bool hasGpu = false;
        foreach (MetricRow row in rows)
        {
            if (row.Instrument == FrameDuration)
            {
                framesPerSecond = row.PerSecond;
                frameMs = row.Value * 1000;
            }
            else if (row.Instrument.EndsWith(GpuMemorySuffix, StringComparison.Ordinal))
            {
                gpuBytes += row.Value;
                hasGpu = true;
            }
            else if (row.Instrument == ProcessMemory)
            {
                processBytes = row.Value;
            }
        }

        string gpu = hasGpu
            ? string.Create(CultureInfo.InvariantCulture, $"GPU {gpuBytes / BytesPerGigabyte:0.0} GB, ")
            : string.Empty;
        _line = string.Create(
            CultureInfo.InvariantCulture,
            $"FPS: {framesPerSecond:0} ({frameMs:0.0} ms) | Memory: {gpu}RAM {processBytes / BytesPerGigabyte:0.0} GB"
        );
    }

    public void Update()
    {
        if (IsShown && _line.Length > 0)
            ui.Text(_line);
    }
}
