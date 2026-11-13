using Magic.Assets;
using Magic.Debugging;

namespace Magic.ImGuiDebugUI;

/// <summary>
/// The lines logged to show on screen, handed to the debug UI's reports. Any thread.
/// </summary>
public sealed class LogSink : ILogSink
{
    private readonly ImGuiDebugUISettings _settings;
    private readonly Reports _reports;

    internal LogSink(ImGuiDebugUISettings settings, Reports reports)
    {
        _settings = settings;
        _reports = reports;
    }

    /// <summary>
    /// Shows a line logged to show on screen for a while: information counted while it shows, and a warning or an error
    /// between <c>!!!</c>s.
    /// </summary>
    public void Write(in Log.Line line)
    {
        if (!line.IsOnScreen)
            return;

        (ReportKind kind, Color color) = line.Level switch
        {
            Log.LoggingLevel.Verbose or Log.LoggingLevel.Info => (ReportKind.Counted, Reports.White),
            Log.LoggingLevel.Warn => (ReportKind.Alarm, Reports.Warning),
            Log.LoggingLevel.Error or Log.LoggingLevel.Critical => (ReportKind.Alarm, Reports.Error),
            _ => (ReportKind.Counted, Reports.White),
        };
        _reports.Add(new Report(line.Message, kind, color, null, null, line.ShowForSec ?? _settings.ReportSec));
    }

    /// <summary>
    /// Nothing: a line shows with the next frame drawn.
    /// </summary>
    public void Flush() { }
}
