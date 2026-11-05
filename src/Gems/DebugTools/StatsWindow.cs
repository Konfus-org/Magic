using System.Globalization;
using Magic.Debugging;
using Magic.Gpu;
using Magic.Projects;

namespace Magic.DebugTools;

/// <summary>
/// A window of how the run is going: the frame rate, the main thread's and the GPU's time, and the memory in use. It adds
/// up while it is open and writes its numbers out twice a second, so they can be read and no text is made per frame.
/// </summary>
internal sealed class StatsWindow(IDebugUI ui, IGraphicsApi? gpu)
{
    private const double RefreshSec = 0.5;
    private const double BytesPerMegabyte = 1024 * 1024;

    private static readonly string[] _columns = ["Name", "Value"];

    private readonly List<string[]> _rows = [];
    private readonly List<string[]> _passRows = [];

    /// <summary>
    /// Each GPU pass's time added up since the last refresh, and how many frames of it, in the order the frame runs them.
    /// </summary>
    private readonly List<PassSum> _passes = [];
    private readonly Dictionary<string, int> _passIndexes = [];

    private bool _isOpen;

    /// <summary>
    /// Whether the window has just opened, so it has numbers from its first frame rather than half a second on.
    /// </summary>
    private bool _isRefreshDue;
    private double _sinceRefreshSec;
    private int _frames;
    private double _worstFrameSec;
    private CpuTimings _cpu;
    private uint _lastGpuFrame;

    /// <summary>
    /// Whether the window shows. Opened, it starts adding up anew, with numbers from the next frame on.
    /// </summary>
    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (value == _isOpen)
                return;

            _isOpen = value;
            Reset();
            _isRefreshDue = true;
        }
    }

    public void Update(in Frame frame)
    {
        if (!_isOpen)
            return;

        Accumulate(frame);
        if (_isRefreshDue || _sinceRefreshSec >= RefreshSec)
        {
            Refresh();
            Reset();
            _isRefreshDue = false;
        }

        Draw();
    }

    private static string FormatMs(double ms) => string.Create(CultureInfo.InvariantCulture, $"{ms:0.00} ms");

    private static string FormatMegabytes(long bytes) =>
        string.Create(CultureInfo.InvariantCulture, $"{bytes / BytesPerMegabyte:0} MB");

    private void Accumulate(in Frame frame)
    {
        _sinceRefreshSec += frame.AppDeltaSec;
        _frames++;
        _worstFrameSec = Math.Max(_worstFrameSec, frame.AppDeltaSec);
        CpuTimings cpu = frame.Cpu;
        _cpu = new CpuTimings(
            _cpu.UpdateMs + cpu.UpdateMs,
            _cpu.FixedUpdateMs + cpu.FixedUpdateMs,
            _cpu.LateUpdateMs + cpu.LateUpdateMs,
            _cpu.RenderMs + cpu.RenderMs,
            _cpu.TotalMs + cpu.TotalMs
        );
        AccumulateGpu();
    }

    /// <summary>
    /// The GPU's timing of each frame it finished, once each, as a frame may see the same one twice.
    /// </summary>
    private void AccumulateGpu()
    {
        if (gpu is null || gpu.Timings.FrameIndex == _lastGpuFrame)
            return;

        GpuTimings timings = gpu.Timings;
        _lastGpuFrame = timings.FrameIndex;
        foreach (GpuTiming timing in timings.Passes)
        {
            if (!_passIndexes.TryGetValue(timing.Name, out int index))
            {
                index = _passes.Count;
                _passIndexes[timing.Name] = index;
                _passes.Add(new PassSum(timing.Name));
            }

            _passes[index].Add(timing.Milliseconds);
        }
    }

    /// <summary>
    /// Writes out what added up since the last refresh.
    /// </summary>
    private void Refresh()
    {
        double frames = _frames;
        double averageSec = _sinceRefreshSec / frames;
        _rows.Clear();
        _rows.Add(["FPS", string.Create(CultureInfo.InvariantCulture, $"{1 / averageSec:0}")]);
        _rows.Add(["Frame", FormatMs(averageSec * 1000)]);
        _rows.Add(["Worst frame", FormatMs(_worstFrameSec * 1000)]);
        _rows.Add(["CPU total", FormatMs(_cpu.TotalMs / frames)]);
        _rows.Add(["CPU Update", FormatMs(_cpu.UpdateMs / frames)]);
        _rows.Add(["CPU FixedUpdate", FormatMs(_cpu.FixedUpdateMs / frames)]);
        _rows.Add(["CPU LateUpdate", FormatMs(_cpu.LateUpdateMs / frames)]);
        _rows.Add(["CPU Render, with waits", FormatMs(_cpu.RenderMs / frames)]);
        RefreshGpuTimes();
        _rows.Add(["Process memory", FormatMegabytes(Environment.WorkingSet)]);
        _rows.Add(["Managed heap", FormatMegabytes(GC.GetTotalMemory(false))]);
        if (gpu is not null)
        {
            string used = FormatMegabytes(gpu.MemoryBytes);
            string total = FormatMegabytes(gpu.Limits.DeviceMemoryBytes);
            _rows.Add(["GPU memory", $"{used} of {total}"]);
        }
    }

    private void RefreshGpuTimes()
    {
        _passRows.Clear();
        if (gpu is null)
            return;

        double totalMs = 0;
        foreach (PassSum pass in _passes)
        {
            double averageMs = pass.AverageMs;
            totalMs += averageMs;
            _passRows.Add([pass.Name, FormatMs(averageMs)]);
        }

        _rows.Add(["GPU total", FormatMs(totalMs)]);
    }

    /// <summary>
    /// Starts adding up again.
    /// </summary>
    private void Reset()
    {
        _sinceRefreshSec = 0;
        _frames = 0;
        _worstFrameSec = 0;
        _cpu = default;
        foreach (PassSum pass in _passes)
        {
            pass.Reset();
        }
    }

    private void Draw()
    {
        ui.Begin("Stats", ref _isOpen, scrollable: true);
        DrawTable("##Stats table", _rows);
        if (gpu is not null && ui.Header(gpu.Limits.DeviceName))
            DrawTable("##GPU passes table", _passRows);

        ui.End();
    }

    private void DrawTable(string label, List<string[]> rows)
    {
        ui.Table(label, _columns);
        foreach (string[] row in rows)
        {
            ui.Row(row);
        }

        ui.EndTable();
    }

    /// <summary>
    /// One GPU pass's time added up over the frames it ran in.
    /// </summary>
    private sealed class PassSum(string name)
    {
        private double _sumMs;
        private int _count;

        public string Name { get; } = name;

        public double AverageMs => _count > 0 ? _sumMs / _count : 0;

        public void Add(double ms)
        {
            _sumMs += ms;
            _count++;
        }

        public void Reset()
        {
            _sumMs = 0;
            _count = 0;
        }
    }
}
