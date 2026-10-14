using System.Globalization;
using System.Numerics;
using Magic.Contexts.Assets;

namespace Magic.ImGuiDebugUI;

/// <summary>
/// What is reported to show for a while: lines on screen, and text at places in the world, each until its seconds after
/// it was last reported are up. The same text at the same place again while it shows keeps it up rather than adding
/// another. Any thread reports; the main thread takes what shows each frame. Time is the caller's, in seconds.
/// </summary>
internal sealed class Reports
{
    private readonly Lock _lock = new();
    private readonly Dictionary<(string Text, Vector3? Position), Report> _reports = [];

    /// <summary>
    /// How many reports show each text, wherever they are.
    /// </summary>
    private readonly Dictionary<string, int> _places = [];

    private long _reported;

    /// <summary>
    /// Shows <paramref name="text"/> until <paramref name="seconds"/> from <paramref name="now"/>, or keeps it showing
    /// that long when it already is; with 0 seconds it shows until the first frame it is not reported in. True when the
    /// text was not showing anywhere, which is when a report that logs logs.
    /// </summary>
    public bool Add(string text, ReportKind kind, Color color, Vector3? position, string? tooltip, float seconds, double now)
    {
        lock (_lock)
        {
            if (_reports.TryGetValue((text, position), out Report? report))
            {
                report.Color = color;
                report.Tooltip = tooltip;
                report.Expires = now + seconds;
                report.IsFresh = true;
                if (kind == ReportKind.Counted)
                {
                    report.Count++;
                    report.Label = string.Create(CultureInfo.InvariantCulture, $"{text} (x{report.Count})");
                }

                return false;
            }

            _reports[(text, position)] = new Report
            {
                Label = kind == ReportKind.Alarm ? $"!!! {text} !!!" : text,
                Color = color,
                Tooltip = tooltip,
                Expires = now + seconds,
                IsFresh = true,
                Count = 1,
                Kind = kind,
                Order = _reported++,
            };
            int places = _places.GetValueOrDefault(text);
            _places[text] = places + 1;
            return places == 0;
        }
    }

    /// <summary>
    /// Adds what shows at <paramref name="now"/> to <paramref name="world"/>, what has a position, and to
    /// <paramref name="lines"/>, what has none, each in the order it was first reported, the lines reported every frame
    /// first. What has had its time, and was not reported again since the last take, is gone after it.
    /// </summary>
    public void Take(double now, List<ShownReport> world, List<ShownReport> lines)
    {
        lock (_lock)
        {
            foreach (((string text, Vector3? position), Report report) in _reports)
            {
                if (!report.IsFresh && now >= report.Expires)
                {
                    // Removing while enumerating a Dictionary is allowed.
                    _reports.Remove((text, position));
                    int places = _places[text] - 1;
                    if (places == 0)
                    {
                        _places.Remove(text);
                    }
                    else
                    {
                        _places[text] = places;
                    }

                    continue;
                }

                report.IsFresh = false;
                ShownReport shown = new(report.Label, report.Color, position ?? default, report.Tooltip, report.Kind == ReportKind.Plain ? long.MinValue + report.Order : report.Order);
                (position is null ? lines : world).Add(shown);
            }
        }

        world.Sort(static (left, right) => left.Order.CompareTo(right.Order));
        lines.Sort(static (left, right) => left.Order.CompareTo(right.Order));
    }

    private sealed class Report
    {
        public required string Label { get; set; }

        public Color Color { get; set; }

        public string? Tooltip { get; set; }

        public double Expires { get; set; }

        /// <summary>
        /// Reported since it was last taken: it shows at least once, however short its time.
        /// </summary>
        public bool IsFresh { get; set; }

        public int Count { get; set; }

        public ReportKind Kind { get; init; }

        public long Order { get; init; }
    }
}

/// <summary>
/// How a report reads on screen.
/// </summary>
internal enum ReportKind : byte
{
    /// <summary>
    /// As it is.
    /// </summary>
    Plain,

    /// <summary>
    /// As it is, with how often it was reported while it showed.
    /// </summary>
    Counted,

    /// <summary>
    /// Between <c>!!!</c>s, a warning or an error.
    /// </summary>
    Alarm,
}

/// <summary>
/// A report as it shows this frame: its text as it reads, where it is in the world (when it has a place), and what shows
/// while the mouse rests on it.
/// </summary>
internal readonly record struct ShownReport(string Label, Color Color, Vector3 Position, string? Tooltip, long Order);
