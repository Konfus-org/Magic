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
    /// <summary>
    /// A header is folded the first time it has more rows than this, so one long table, such as the GPU's time per
    /// pass, does not push the rest of its tab out of view. Above the rows Core's hook timings have.
    /// </summary>
    private const int FoldedRows = 40;

    private static readonly string[] _columns = ["Name", "Value", "Per second", "Max"];

    private readonly Telemetry _telemetry = new(settings);

    /// <summary>
    /// The headers folded for having too many rows: each is folded once, then opens and closes as it is clicked.
    /// </summary>
    private readonly HashSet<string> _folded = new(StringComparer.Ordinal);
    private int _tab;

    public void Dispose() => _telemetry.Dispose();

    internal void Update()
    {
        bool isOpen = window.Begin(out bool copy);
        _telemetry.Listen(isOpen);

        if (!isOpen) return;

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

        if (copy) window.Copy(_telemetry.GetAllText());

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

            if (!string.Equals(line.Tab, tab, StringComparison.Ordinal)) continue;

            if (!string.Equals(line.Header, header, StringComparison.Ordinal))
            {
                if (isUnfolded) ui.EndTable();

                // A new header counts its rows, and is folded the first time it has too many to show.
                header = line.Header;
                int rows = 1;
                while (i + rows < lines.Count
                    && string.Equals(lines[i + rows].Header, header, StringComparison.Ordinal))
                {
                    rows++;
                }

                isUnfolded = ui.Header(header, folds: rows > FoldedRows && _folded.Add(header));
                if (isUnfolded) ui.Table(header, _columns);
            }

            if (!isUnfolded) continue;

            ui.Row([line.Name, line.Value, line.Rate, line.Max]);
            if (line.Description is { Length: > 0 } description) ui.Tooltip(description);
        }

        if (isUnfolded) ui.EndTable();
    }
}
