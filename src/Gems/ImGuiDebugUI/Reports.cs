using System.Globalization;
using System.Numerics;
using System.Threading.Channels;
using Magic.Assets;

namespace Magic.ImGuiDebugUI;

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
    /// Between <c>!!!</c>s.
    /// </summary>
    Alarm,
}

/// <summary>
/// One report as it was made.
/// </summary>
internal readonly record struct Report(
    string Text,
    ReportKind Kind,
    Color Color,
    Vector3? Position,
    string? Tooltip,
    float StaySec
);

/// <summary>
/// A report as it shows this frame: its text as it reads, where it is in the world (when it has a place), and what shows
/// while the mouse rests on it.
/// </summary>
internal readonly record struct ShownReport(string Label, Color Color, Vector3 Position, string? Tooltip, long Order);

/// <summary>
/// What is reported to show for a while: lines on screen, and text at places in the world, each until its seconds after it
/// was last reported are up. The same text at the same place again while it shows keeps it up rather than adding another.
/// Any thread reports into a channel; the game thread drains it as it takes what shows each frame.
/// </summary>
internal sealed class Reports
{
    // What reports are written in, linear as Color is: white, an orange warning and a red error.
    internal static readonly Color White = new(1, 1, 1);
    internal static readonly Color Warning = new(1, 0.376f, 0);
    internal static readonly Color Error = new(1, 0, 0);

    private readonly Channel<Report> _arrived = Channel.CreateUnbounded<Report>(
        new UnboundedChannelOptions { SingleReader = true }
    );
    private readonly Dictionary<(string Text, Vector3? Position), Showing> _showing = [];
    private long _reported;

    /// <summary>
    /// Hands <paramref name="report"/> to the next take. Any thread.
    /// </summary>
    internal void Add(in Report report) => _arrived.Writer.TryWrite(report);

    /// <summary>
    /// Adds what shows at <paramref name="nowSec"/> to <paramref name="world"/>, what has a position, and to
    /// <paramref name="lines"/>, what has none: first what is reported every frame, in the order it was last reported, then
    /// the rest, in the order it was first reported. The rest, the lines logged, is left out unless
    /// <paramref name="isLogShown"/>, but its time still runs. What has had its time, and was not reported again since the
    /// last take, is gone after it. Game thread.
    /// </summary>
    internal void Take(double nowSec, bool isLogShown, List<ShownReport> world, List<ShownReport> lines)
    {
        // What arrived keeps what shows the same up for its seconds from now, or starts showing.
        while (_arrived.Reader.TryRead(out Report report))
        {
            if (_showing.TryGetValue((report.Text, report.Position), out Showing? showing))
            {
                showing.Color = report.Color;
                showing.Tooltip = report.Tooltip;
                showing.ExpiresSec = nowSec + report.StaySec;
                showing.IsFresh = true;
                if (report.Kind == ReportKind.Plain)
                    showing.Order = _reported++;

                if (report.Kind == ReportKind.Counted)
                {
                    showing.Count++;
                    showing.Label = string.Create(CultureInfo.InvariantCulture, $"{report.Text} (x{showing.Count})");
                }

                continue;
            }

            _showing[(report.Text, report.Position)] = new Showing
            {
                Label = report.Kind == ReportKind.Alarm ? $"!!! {report.Text} !!!" : report.Text,
                Color = report.Color,
                Tooltip = report.Tooltip,
                ExpiresSec = nowSec + report.StaySec,
                IsFresh = true,
                Count = 1,
                Kind = report.Kind,
                Order = _reported++,
            };
        }

        // What has had its time goes; the rest shows, and is taken as not reported since.
        foreach (((string text, Vector3? position), Showing showing) in _showing)
        {
            if (!showing.IsFresh && nowSec >= showing.ExpiresSec)
            {
                // Removing while enumerating a Dictionary is allowed.
                _showing.Remove((text, position));

                continue;
            }

            // Plain text, reported every frame, comes before the rest, in the order of this frame's reports.
            showing.IsFresh = false;
            if (showing.Kind != ReportKind.Plain && !isLogShown)
                continue;

            ShownReport shown = new(
                showing.Label,
                showing.Color,
                position ?? default,
                showing.Tooltip,
                showing.Kind == ReportKind.Plain ? long.MinValue + showing.Order : showing.Order
            );
            (position is null ? lines : world).Add(shown);
        }

        world.Sort(static (left, right) => left.Order.CompareTo(right.Order));
        lines.Sort(static (left, right) => left.Order.CompareTo(right.Order));
    }

    private sealed class Showing
    {
        internal required string Label { get; set; }

        internal Color Color { get; set; }

        internal string? Tooltip { get; set; }

        internal double ExpiresSec { get; set; }

        /// <summary>
        /// Reported since it was last taken: it shows at least once, however short its time.
        /// </summary>
        internal bool IsFresh { get; set; }

        internal int Count { get; set; }

        internal ReportKind Kind { get; init; }

        internal long Order { get; set; }
    }
}
