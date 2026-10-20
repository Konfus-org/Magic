using Magic.Interfaces;

namespace Magic.DebugTools;

/// <summary>
/// The telemetry: what the engine measures, summed up over the last second (<see cref="Telemetry"/>), a tab per entry
/// of the settings' tabs and a header per meter and area under it, which a click folds away, holding a table of its
/// rows, a column each for the name, the value, how many or how much a second, and the most, with an instrument's
/// description shown while the mouse rests on its rows. It listens only while it is open, so nothing is measured for
/// it otherwise; Copy gives every tab.
/// </summary>
internal sealed class TelemetryWindow(ToolWindow window, IDebugUI ui, DebugToolsSettings settings) : IDisposable
{
    private static readonly string[] _columns = ["Name", "Value", "Per second", "Max"];

    private readonly Telemetry _telemetry = new(settings);
    private int _tab;

    public void Dispose()
    {
        _telemetry.Dispose();
    }

    public void Update()
    {
        bool isOpen = window.Begin(out bool copy);
        _telemetry.Listen(isOpen);
        if (!isOpen)
        {
            return;
        }

        _telemetry.Refresh();
        ReadOnlySpan<string> tabs = _telemetry.Tabs;
        if (tabs.IsEmpty)
        {
            ui.Label("Measuring for a second...");
        }
        else
        {
            ui.Tabs("##tabs", ref _tab, tabs);
            Draw(tabs[Math.Clamp(_tab, 0, tabs.Length - 1)]);
        }

        if (copy)
        {
            window.Copy(_telemetry.AllText());
        }

        window.End();
    }

    /// <summary>
    /// <paramref name="tab"/>'s rows, a table under a header per meter and area.
    /// </summary>
    private void Draw(string tab)
    {
        IReadOnlyList<TelemetryLine> lines = _telemetry.Lines;
        string header = string.Empty;
        bool isUnfolded = false;
        for (int i = 0; i < lines.Count; i++)
        {
            TelemetryLine line = lines[i];
            if (!string.Equals(line.Tab, tab, StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.Equals(line.Header, header, StringComparison.Ordinal))
            {
                if (isUnfolded)
                {
                    ui.EndTable();
                }

                header = line.Header;
                isUnfolded = ui.Header(header);
                if (isUnfolded)
                {
                    ui.Table(header, _columns);
                }
            }

            if (!isUnfolded)
            {
                continue;
            }

            ui.Row([line.Name, line.Value, line.Rate, line.Max]);
            if (line.Description is { Length: > 0 } description)
            {
                ui.Tooltip(description);
            }
        }

        if (isUnfolded)
        {
            ui.EndTable();
        }
    }
}
