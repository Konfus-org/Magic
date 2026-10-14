using Magic.Interfaces;

namespace Magic.DebugTools;

/// <summary>
/// The telemetry: what the engine measures, summed up over the last second (<see cref="Telemetry"/>), a tab per meter and
/// a header per area, which a click folds away, with an instrument's description shown while the mouse rests on its
/// lines. It listens only while it is open, so nothing is measured for it otherwise; Copy gives every tab.
/// </summary>
internal sealed class TelemetryWindow(ToolWindow window, IDebugUI ui, DebugToolsSettings settings) : IDisposable
{
    private readonly Telemetry _telemetry = new(settings);
    private int _tab;

    public void Dispose()
    {
        _telemetry.Dispose();
    }

    public void Update()
    {
        bool isOpen = window.Begin(scrollable: true);
        _telemetry.Listen(isOpen);
        if (!isOpen)
        {
            return;
        }

        _telemetry.Refresh();
        ReadOnlySpan<string> meters = _telemetry.Meters;
        if (meters.IsEmpty)
        {
            ui.Label("Measuring for a second...");
        }
        else
        {
            ui.Tabs("##meters", ref _tab, meters);
            Draw(meters[Math.Clamp(_tab, 0, meters.Length - 1)]);
        }

        if (window.IsCopyClicked)
        {
            window.Copy(_telemetry.AllText());
        }

        window.End();
    }

    /// <summary>
    /// <paramref name="meter"/>'s lines, under a header per area.
    /// </summary>
    private void Draw(string meter)
    {
        IReadOnlyList<TelemetryLine> lines = _telemetry.Lines;
        string area = string.Empty;
        bool isUnfolded = true;
        for (int i = 0; i < lines.Count; i++)
        {
            TelemetryLine line = lines[i];
            if (!string.Equals(line.Meter, meter, StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.Equals(line.Area, area, StringComparison.Ordinal))
            {
                area = line.Area;
                isUnfolded = ui.Header(area);
            }

            if (!isUnfolded)
            {
                continue;
            }

            ui.Label(line.Text);
            if (line.Description is { Length: > 0 } description)
            {
                ui.Tooltip(description);
            }
        }
    }
}
