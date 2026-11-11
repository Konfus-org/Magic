using System.Globalization;
using System.Linq;
using System.Text.Json;
using Magic.Debugging;

namespace Magic.DebugTools;

/// <summary>
/// A window of everything the meters measure, in sections that fold: CPU, the game thread's frame and the render
/// thread's, with what Core and each gem spend of them; GPU, what the GPU provider says of the API and the GPU, and its frame and passes; Memory, Core's
/// and the process's, then each gem's; and Other, the rest by meter. A histogram's row shows its mean, how many a second
/// and its most; a counter's how much it rose and how much a second; a gauge's its latest value. Seconds show as
/// milliseconds and bytes as megabytes. It also writes the <see cref="CounterLine"/> from the same rows.
/// </summary>
internal sealed class StatsWindow(IDebugUI ui)
{
    private const double BytesPerMegabyte = 1024 * 1024;
    private const double BytesPerGigabyte = 1024 * 1024 * 1024;
    private const string GemMeterPrefix = "Magic.Gems.";
    private const string WorkingSet = "dotnet.process.memory.working_set";

    /// <summary>
    /// OpenTelemetry's name for the RAM the process holds, which the platform gem publishes as only this process's.
    /// </summary>
    private const string ProcessMemoryUsage = "process.memory.usage";
    private const string GpuPrefix = "gpu.";
    private const string GpuFrameDuration = "gpu.frame.duration";
    private const string MemoryUsageSuffix = "memory.usage";

    /// <summary>
    /// Core's part, in the CPU and Memory sections.
    /// </summary>
    private const string Core = "Core";

    private static readonly string[] _columns = ["Name", "Value", "Per second", "Max"];

    /// <summary>
    /// What the meters say of themselves that isn't the GPU's, above the sections.
    /// </summary>
    private readonly List<string> _lines = [];

    /// <summary>
    /// The sections, written out at the last refresh.
    /// </summary>
    private readonly List<Section> _sections = [];

    /// <summary>
    /// The frame rate and the memory in use, as written out at the last refresh:
    /// <c>FPS: 59 (17.0 ms) | Memory: GPU 0.4 GB, RAM 0.9 GB</c>. The frame is the one the render thread draws.
    /// </summary>
    public string CounterLine { get; private set; } = string.Empty;

    /// <summary>
    /// The sections and the counter line written out again from the last collection's <paramref name="rows"/>.
    /// </summary>
    public void Refresh(IReadOnlyList<MetricRow> rows)
    {
        _lines.Clear();
        _sections.Clear();
        Section cpu = new("cpu", "CPU", isOpenAtStart: true);
        Section gpu = new("gpu", "GPU", isOpenAtStart: true);
        Section memory = new("memory", "Memory", isOpenAtStart: true);
        Section other = new("other", "Other", isOpenAtStart: false);
        _sections.AddRange([cpu, gpu, memory, other]);

        // Core's memory first, ahead of the gems'.
        memory.GetChild(Core);

        // The meters that measure the GPU are described in its section, the rest above them all.
        HashSet<string> gpuMeters = [];
        foreach (MetricRow row in rows)
        {
            if (StripMeter(row.Meter, row.Instrument).StartsWith(GpuPrefix, StringComparison.Ordinal))
                gpuMeters.Add(row.Meter);
        }

        HashSet<string> described = [];
        double framesPerSecond = 0;
        double drawnPerSecond = 0;
        double drawnSec = 0;
        double workSec = 0;
        double gpuSec = 0;
        double privateBytes = 0;
        double workingSetBytes = 0;
        double gpuBytes = 0;
        foreach (MetricRow row in rows)
        {
            string name = StripMeter(row.Meter, row.Instrument);
            if (described.Add(row.Meter))
                (gpuMeters.Contains(row.Meter) ? gpu.Lines : _lines).AddRange(Describe(row));

            if (row.Unit == "By")
            {
                Section part = memory.GetChild(GetPart(row.Meter));
                part.Rows.Add(ToCells(GetLabel(name, row), row));
                if (name == ProcessMemoryUsage)
                {
                    privateBytes = row.Value;
                }
                else if (name.EndsWith(MemoryUsageSuffix, StringComparison.Ordinal))
                {
                    part.Amount += row.Value;
                    gpuBytes += name.StartsWith(GpuPrefix, StringComparison.Ordinal) ? row.Value : 0;
                }
                else if (row.Instrument == WorkingSet)
                {
                    part.Amount += row.Value;
                    workingSetBytes = row.Value;
                }
            }
            else if (row.Instrument is Metrics.HookDuration or Metrics.StepDuration)
            {
                // A gem's hook or a step of Core's, which add up to the time a frame it takes.
                bool isHook = row.Instrument == Metrics.HookDuration;
                Section part = cpu.GetChild(isHook ? row.GetTag(Metrics.GemNameTag) : Core);
                part.Rows.Add(ToCells(row.GetTag(isHook ? Metrics.HookTag : Metrics.StepTag), row));
                part.Amount += row.Value * row.PerSecond;
            }
            else if (
                row.Instrument is Metrics.FrameDuration or Metrics.FrameWorkDuration or Metrics.RenderFrameDuration
            )
            {
                cpu.Rows.Add(ToCells(name, row));
                if (row.Instrument == Metrics.FrameDuration)
                    framesPerSecond = row.PerSecond;
                else if (row.Instrument == Metrics.FrameWorkDuration)
                    workSec = row.Value;
                else
                    (drawnPerSecond, drawnSec) = (row.PerSecond, row.Value);
            }
            else if (name.StartsWith(GpuPrefix, StringComparison.Ordinal))
            {
                // Tagged, as passes are, each instrument under a header of its own.
                string own = name[GpuPrefix.Length..];
                if (row.Tags.Count == 0)
                    gpu.Rows.Add(ToCells(own, row));
                else
                    gpu.GetChild(own).Rows.Add(ToCells(row.TagText, row));

                if (name == GpuFrameDuration)
                    gpuSec = row.Value;
            }
            else
            {
                other.GetChild(row.Meter).Rows.Add(ToCells(GetLabel(name, row), row));
            }
        }

        cpu.SetLabel($"CPU: {FormatAmount(workSec, "s")}");

        // The part that takes the most first, to find what slows a frame at a glance.
        cpu.Children.Sort((a, b) => b.Amount.CompareTo(a.Amount));
        foreach (Section part in cpu.Children)
        {
            double perFrame = framesPerSecond > 0 ? part.Amount / framesPerSecond : 0;
            part.SetLabel($"{part.Name}: {FormatAmount(perFrame, "s")}");
        }

        gpu.SetLabel($"GPU: {FormatAmount(gpuSec, "s")}");

        // The process's own RAM where the platform gem says it, else .NET's working set, shared pages and all.
        double processBytes = privateBytes > 0 ? privateBytes : workingSetBytes;
        string gpuMemory = gpuBytes > 0 ? FormatGigabytes(", GPU", gpuBytes) : string.Empty;
        memory.SetLabel($"Memory: {FormatGigabytes("RAM", processBytes)}{gpuMemory}");
        foreach (Section part in memory.Children)
        {
            part.SetLabel(part.Amount > 0 ? $"{part.Name}: {FormatAmount(part.Amount, "By")}" : part.Name);
        }

        string gpuCounter = gpuBytes > 0 ? FormatGigabytes("GPU", gpuBytes) + ", " : string.Empty;
        CounterLine = string.Create(
            CultureInfo.InvariantCulture,
            $"FPS: {drawnPerSecond:0} ({drawnSec * 1000:0.0} ms) | Memory: {gpuCounter}{FormatGigabytes("RAM", processBytes)}"
        );
    }

    /// <summary>
    /// Draws the window while <paramref name="isOpen"/>, which its close button clears.
    /// </summary>
    public void Update(ref bool isOpen)
    {
        if (!isOpen)
            return;

        ui.Begin("Stats", ref isOpen, scrollable: true);
        foreach (string line in _lines)
        {
            ui.Label(line);
        }

        foreach (Section section in _sections)
        {
            Draw(section, 0);
        }

        ui.End();
    }

    /// <summary>
    /// <paramref name="name"/> without its meter's, as instruments and tags are named after their meter in snake case
    /// (<c>magic.core.</c> for <c>Magic.Core</c>), or without its first part when named otherwise, as .NET's are.
    /// </summary>
    private static string StripMeter(string meter, string name)
    {
        string[] parts = Array.ConvertAll(meter.Split('.'), JsonNamingPolicy.SnakeCaseLower.ConvertName);
        string own = string.Join('.', parts) + ".";

        return name.StartsWith(own, StringComparison.Ordinal)
            ? name[own.Length..]
            : name[(name.IndexOf('.', StringComparison.Ordinal) + 1)..];
    }

    /// <summary>
    /// The part a meter measures: the gem a <c>Magic.Gems.*</c> meter is named after, or Core for Core's and .NET's.
    /// </summary>
    private static string GetPart(string meter) =>
        meter.StartsWith(GemMeterPrefix, StringComparison.Ordinal) ? meter[GemMeterPrefix.Length..] : Core;

    private static string GetLabel(string name, in MetricRow row) =>
        row.Tags.Count == 0 ? name : $"{name} {row.TagText}";

    private static string[] ToCells(string label, in MetricRow row) =>
        [
            label,
            FormatAmount(row.Value, row.Unit),
            row.Kind == MetricKind.Latest ? string.Empty : FormatRate(row),
            row.Kind == MetricKind.Distribution ? FormatAmount(row.Max, row.Unit) : string.Empty,
        ];

    /// <summary>
    /// <paramref name="value"/> in <paramref name="unit"/>: seconds as milliseconds, as most of what is timed in a frame
    /// takes far less than one, bytes as megabytes, and a count's unit, in braces, left out.
    /// </summary>
    private static string FormatAmount(double value, string unit) =>
        unit switch
        {
            "s" => string.Create(CultureInfo.InvariantCulture, $"{value * 1000:0.###} ms"),
            "By" => string.Create(CultureInfo.InvariantCulture, $"{value / BytesPerMegabyte:0.#} MB"),
            "" or ['{', ..] => value.ToString("0.###", CultureInfo.InvariantCulture),
            _ => string.Create(CultureInfo.InvariantCulture, $"{value:0.###} {unit}"),
        };

    private static string FormatGigabytes(string label, double bytes) =>
        string.Create(CultureInfo.InvariantCulture, $"{label} {bytes / BytesPerGigabyte:0.0} GB");

    /// <summary>
    /// How many a second for a histogram, how much a second for a counter.
    /// </summary>
    private static string FormatRate(in MetricRow row) =>
        row.Kind == MetricKind.Distribution
            ? string.Create(CultureInfo.InvariantCulture, $"{row.PerSecond:0.#}/s")
            : $"{FormatAmount(row.PerSecond, row.Unit)}/s";

    /// <summary>
    /// What <paramref name="row"/>'s meter says of itself: <c>device type: discrete</c> for
    /// <c>magic.gems.vulkan_graphics.device.type</c>.
    /// </summary>
    private static IEnumerable<string> Describe(MetricRow row) =>
        row.MeterTags.Select(tag => $"{StripMeter(row.Meter, tag.Key).Replace('.', ' ')}: {tag.Value}");

    /// <summary>
    /// <paramref name="section"/>'s header, then, while it is open, its lines, its rows and its sections, a step in.
    /// An empty section is left out.
    /// </summary>
    private void Draw(Section section, int depth)
    {
        if (section.Lines.Count == 0 && section.Rows.Count == 0 && section.Children.Count == 0)
            return;

        if (!ui.Header(section.Header, section.IsOpenAtStart, depth))
            return;

        foreach (string line in section.Lines)
        {
            ui.Label(line);
        }

        if (section.Rows.Count > 0)
        {
            ui.Table(section.Id, _columns);
            foreach (string[] row in section.Rows)
            {
                ui.Row(row);
            }

            ui.EndTable();
        }

        foreach (Section child in section.Children)
        {
            Draw(child, depth + 1);
        }
    }

    /// <summary>
    /// A part of the window under a header: its lines, its rows, and the sections in it, which start folded. Its
    /// <see cref="Amount"/> sums what its header shows: seconds a second for a part of the CPU's, bytes for memory.
    /// </summary>
    private sealed class Section(string id, string name, bool isOpenAtStart)
    {
        public string Id { get; } = id;

        public string Name { get; } = name;

        public bool IsOpenAtStart => isOpenAtStart;

        /// <summary>
        /// What its header reads, with <see cref="Id"/> as its ImGui id, so the header keeps whether it is open as its
        /// numbers change.
        /// </summary>
        public string Header { get; private set; } = $"{name}###{id}";

        public double Amount { get; set; }

        public List<string> Lines { get; } = [];

        public List<string[]> Rows { get; } = [];

        public List<Section> Children { get; } = [];

        public void SetLabel(string label) => Header = $"{label}###{Id}";

        /// <summary>
        /// The section in this one named <paramref name="childName"/>, added the first time.
        /// </summary>
        public Section GetChild(string childName)
        {
            foreach (Section child in Children)
            {
                if (child.Name == childName)
                    return child;
            }

            Section added = new($"{Id}.{childName}", childName, isOpenAtStart: false);
            Children.Add(added);

            return added;
        }
    }
}
