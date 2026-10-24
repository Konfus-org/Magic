using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Magic.Attributes;
using Magic.Contexts.Assets;
using Magic.Contexts.Settings;
using Magic.Contexts.Subscriptions;
using Magic.Contexts.Utils;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.DebugTools;

/// <summary>
/// The settings: the preset applied, which can be switched; a filter, which while it has text shows every setting it
/// finds in place of the tabs; then a tab per category of settings (<see cref="CategoryAttribute"/> up to its slash,
/// else the gem's name), in it a header per sub-category (past the slash, else the gem's name), which a click folds
/// away, and under it a row per setting (<see cref="Settings.All"/>): labelled in words, its key and description shown
/// while the mouse rests on it, a number kept to its <see cref="RangeAttribute"/>. A setting marked
/// <see cref="RequireRestartAttribute"/> says on its row that a change starts the engine again. An edit is set through
/// <see cref="Settings.TrySet{T}"/>, live until the next preset; nothing is saved. Copy gives the open tab, or what the
/// filter finds, as <c>Gem.setting=value</c> lines, the form <c>--set</c> takes. Its commands: <c>get Gem</c>,
/// <c>set Gem.setting=value</c> and <c>preset [Name]</c>.
/// </summary>
internal sealed class SettingsWindow : IDisposable
{
    private readonly ToolWindow _window;
    private readonly IDebugUI _ui;
    private readonly Settings _settings;
    private readonly CancellationToken _stopping;
    private readonly SubscriptionList _commands = new();
    private readonly List<Row> _rows = [];

    /// <summary>
    /// How many headers each tab has: a tab with one shows none.
    /// </summary>
    private readonly Dictionary<string, int> _headerCounts = new(StringComparer.Ordinal);

    /// <summary>
    /// Every preset there is, as the preset choice shows them, by name, and the name of each.
    /// </summary>
    private readonly Handle<Preset>[] _presets;
    private readonly string[] _presetNames;

    private string[] _tabs = [];
    private int _tab;

    /// <summary>
    /// What is typed in the filter: while there is any, the rows it finds show in place of the tabs.
    /// </summary>
    private string _filter = string.Empty;

    internal SettingsWindow(
        ToolWindow window,
        IDebugUI ui,
        Settings settings,
        Assets assets,
        ICommands? commands,
        CancellationToken stopping)
    {
        _window = window;
        _ui = ui;
        _settings = settings;
        _stopping = stopping;

        // The presets as their choice shows them: sorted by name, each handle moved with its name.
        _presets = [.. settings.Presets];
        _presetNames = Array.ConvertAll(
            _presets,
            preset => Path.GetFileNameWithoutExtension(assets.GetPath(preset)) ?? string.Empty);
        Array.Sort(_presetNames, _presets, StringComparer.OrdinalIgnoreCase);

        if (commands is null) return;

        _commands.Add(commands.Register(
            "get",
            "Logs every setting of a gem as Gem.setting=value lines: get DebugTools.",
            RunGet));
        _commands.Add(commands.Register(
            "set",
            "Changes a setting while running, until the next preset: set DebugTools.isFrameRateShown=false.",
            RunSet));
        _commands.Add(commands.Register(
            "preset",
            "Logs the preset applied and every preset there is; with a name, applies that preset.",
            RunPreset));
    }

    public void Dispose() => _commands.Dispose();

    internal void Update()
    {
        if (!_window.Begin(out bool copy)) return;

        IReadOnlyList<Setting> all = _settings.All;
        Collect(all);

        DrawPreset();
        _ui.Input("##filter", ref _filter, hint: "Filter", focus: true);
        _ui.Tooltip("Every setting, in any tab, whose name, key or description has this.");

        if (_tabs.Length == 0)
        {
            _ui.Label("No gem takes settings.");
        }
        else if (_filter.Length > 0)
        {
            DrawRows(all);
            if (copy) _window.Copy(GetLines(GetShown(all)));
        }
        else
        {
            _ui.Tabs("##gems", ref _tab, _tabs);
            _tab = Math.Clamp(_tab, 0, _tabs.Length - 1);
            DrawRows(all, _tabs[_tab]);
            if (copy) _window.Copy(GetLines(GetShown(all, _tabs[_tab])));
        }

        _window.End();
    }

    /// <summary>
    /// A name in words: <c>RenderScale</c> and <c>renderScale</c> are "Render scale"; an acronym stays as it is
    /// (<c>LodBiasLOD</c> is "Lod bias LOD").
    /// </summary>
    private static string ToWords(string name)
    {
        StringBuilder words = new(name.Length + 8);
        for (int i = 0; i < name.Length; i++)
        {
            // A capital after a lower-case letter or a digit starts a word, as does an acronym's last capital with
            // lower case after it; it is lowered unless a capital follows it, as in an acronym.
            char character = name[i];
            bool isAfterLower = i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]));
            bool isEndOfAcronym = i > 0 && char.IsUpper(name[i - 1]) && i + 1 < name.Length
                && char.IsLower(name[i + 1]);
            if (i == 0)
            {
                words.Append(char.ToUpperInvariant(character));
            }
            else if (char.IsUpper(character) && (isAfterLower || isEndOfAcronym))
            {
                bool isAcronym = i + 1 < name.Length && char.IsUpper(name[i + 1]);
                words.Append(' ').Append(isAcronym ? character : char.ToLowerInvariant(character));
            }
            else
            {
                words.Append(character);
            }
        }

        return words.ToString();
    }

    /// <summary>
    /// Where a setting sits: its tab, its category up to its slash, <c>Render</c> of <c>Render/Shadows</c>, and its
    /// header in the tab, the category past the slash, <c>Shadows</c>; its gem's name stands in for whichever the
    /// category does not say.
    /// </summary>
    private static (string Tab, string Header) GetPlace(Setting setting)
    {
        string? category = setting.Attributes.OfType<CategoryAttribute>().FirstOrDefault()?.Category;
        int slash = category?.IndexOf('/', StringComparison.Ordinal) ?? -1;

        if (category is null) return (setting.Owner, setting.Owner);
        if (slash < 0) return (category, setting.Owner);

        return (category[..slash], category[(slash + 1)..]);
    }

    /// <summary>
    /// What is typed for a setting, as the console takes it: JSON when it parses as JSON (1920, true, ["a"],
    /// "quoted"), and a string when it does not.
    /// </summary>
    private static JsonNode? ParseValue(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return JsonValue.Create(text);
        }
    }

    /// <summary>
    /// Where a tab goes: Render first and Debug last, the rest between them as they were first met.
    /// </summary>
    private static int GetTabOrder(string tab, string[] met)
    {
        if (string.Equals(tab, "Render", StringComparison.Ordinal)) return -1;
        if (string.Equals(tab, "Debug", StringComparison.Ordinal)) return met.Length;

        return Array.IndexOf(met, tab);
    }

    /// <summary>
    /// <paramref name="settings"/> as <c>Gem.setting=value</c> lines.
    /// </summary>
    private static string GetLines(IEnumerable<Setting> settings)
    {
        StringBuilder text = new();
        foreach (Setting setting in settings)
        {
            text.Append(setting.Key).Append('=')
                .Append(JsonSerializer.Serialize(setting.Value, setting.Type, Json.Options))
                .Append('\n');
        }

        return text.ToString();
    }

    /// <summary>
    /// Whether <paramref name="row"/>, of <paramref name="setting"/>, shows: in <paramref name="tab"/>, or, without a
    /// tab, when the filter finds it in its name in words, its key or its description, in any case.
    /// </summary>
    private bool IsShown(Row row, Setting setting, string? tab)
    {
        if (tab is not null) return string.Equals(row.Tab, tab, StringComparison.Ordinal);

        return row.Label.Contains(_filter, StringComparison.OrdinalIgnoreCase)
            || setting.Key.Contains(_filter, StringComparison.OrdinalIgnoreCase)
            || setting.Description.Contains(_filter, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The settings of the rows that show, in their order (<see cref="IsShown"/>).
    /// </summary>
    private IEnumerable<Setting> GetShown(IReadOnlyList<Setting> all, string? tab = null)
    {
        foreach (Row row in _rows)
        {
            if (IsShown(row, all[row.Index], tab)) yield return all[row.Index];
        }
    }

    /// <summary>
    /// <paramref name="tab"/>'s rows, under a header per sub-category or gem, none when the tab has only one; without
    /// a tab, the rows of every tab the filter finds, under a header per tab and sub-category, in place of the tabs. A
    /// click on a header folds it away.
    /// </summary>
    private void DrawRows(IReadOnlyList<Setting> all, string? tab = null)
    {
        bool hasHeaders = tab is null || _headerCounts.GetValueOrDefault(tab) > 1;
        string header = string.Empty;
        bool isUnfolded = true;
        foreach (Row row in _rows)
        {
            Setting setting = all[row.Index];

            if (!IsShown(row, setting, tab)) continue;

            string under = tab is null ? row.Heading : row.Header;
            if (hasHeaders && !string.Equals(under, header, StringComparison.Ordinal))
            {
                header = under;
                isUnfolded = _ui.Header(header);
            }

            if (!isUnfolded) continue;

            DrawRow(row, setting);
            _ui.Tooltip(row.Tip);
        }
    }

    /// <summary>
    /// The rows of every gem's live settings and the tabs they make, gathered the first time the window opens, by when
    /// every gem is made, so once there are rows there is nothing to gather. A row keeps where its setting sits in
    /// <paramref name="all"/>, whose order is fixed. Tabs come in the order they are first met, and within a tab a
    /// header's rows are kept together, the headers in the order they are first met too.
    /// </summary>
    private void Collect(IReadOnlyList<Setting> all)
    {
        if (_rows.Count > 0) return;

        List<Row> rows = [];
        List<string> tabs = [];
        List<(string Tab, string Header)> headers = [];
        for (int i = 0; i < all.Count; i++)
        {
            Setting setting = all[i];
            (string tab, string header) = GetPlace(setting);
            RangeAttribute? range = setting.Attributes.OfType<RangeAttribute>().FirstOrDefault();
            bool restarts = setting.Attributes.OfType<RequireRestartAttribute>().Any();
            string label = ToWords(setting.Name);
            string tip = setting.Description.Length > 0 ? $"{setting.Key}: {setting.Description}" : setting.Key;
            Row row = new()
            {
                Index = i,
                Tab = tab,
                Header = header,
                Heading = string.Equals(tab, header, StringComparison.Ordinal) ? tab : $"{tab}: {header}",
                Label = label,
                Id = $"{label}##{setting.Key}",
                Tip = restarts
                    ? $"{tip} Changed, the engine starts again at once, every setting as it is: the world is saved and "
                        + "put back, so the game goes on where it was."
                    : tip,
                Min = range is null ? 0 : Convert.ToSingle(range.Minimum, CultureInfo.InvariantCulture),
                Max = range is null ? 0 : Convert.ToSingle(range.Maximum, CultureInfo.InvariantCulture),
                Choices = setting.Type.IsEnum ? [.. Enum.GetValues(setting.Type).Cast<object>()] : [],
                Labels = setting.Type.IsEnum ? Array.ConvertAll(Enum.GetNames(setting.Type), ToWords) : [],
            };
            rows.Add(row);
            if (!tabs.Contains(row.Tab, StringComparer.Ordinal)) tabs.Add(row.Tab);

            if (!headers.Contains((row.Tab, row.Header))) headers.Add((row.Tab, row.Header));
        }

        string[] met = [.. tabs];
        tabs.Sort((left, right) => GetTabOrder(left, met).CompareTo(GetTabOrder(right, met)));
        _rows.AddRange(
            rows.OrderBy(row => tabs.IndexOf(row.Tab)).ThenBy(row => headers.IndexOf((row.Tab, row.Header))));
        _tabs = [.. tabs];
        foreach ((string tab, _) in headers)
        {
            _headerCounts[tab] = _headerCounts.GetValueOrDefault(tab) + 1;
        }
    }

    /// <summary>
    /// The preset applied, as a choice of every preset there is; choosing one applies it.
    /// </summary>
    private void DrawPreset()
    {
        int index = Array.IndexOf(_presets, _settings.Preset);

        if (!_ui.Choice("Preset", ref index, _presetNames) || index < 0) return;

        ApplyAsync(_presets[index], _stopping).FireAndForget();
    }

    /// <summary>
    /// A row's widget for what its setting holds: a checkbox, a slider, a choice, or a text field, which is a string's
    /// text or anything else's JSON and is set when Enter is pressed in it. A setting read only as its gem starts is
    /// its value alone.
    /// </summary>
    private void DrawRow(Row row, Setting setting)
    {
        switch (setting.Value)
        {
            case bool flag:
                if (_ui.Checkbox(row.Id, ref flag)) Change(setting, flag);

                break;
            case float number:
                if (_ui.Slider(row.Id, ref number, row.Min, row.Max)) Change(setting, number);

                break;
            case double number:
                float slid = (float)number;
                if (_ui.Slider(row.Id, ref slid, row.Min, row.Max)) Change(setting, (double)slid);

                break;
            case int whole:
                float slidWhole = whole;
                if (_ui.Slider(row.Id, ref slidWhole, row.Min, row.Max)) Change(setting, (int)MathF.Round(slidWhole));

                break;
            case Enum choice:
                int index = Array.IndexOf(row.Choices, choice);
                if (_ui.Choice(row.Id, ref index, row.Labels) && index >= 0) Change(setting, row.Choices[index]);

                break;
            default:
                DrawText(row, setting);
                break;
        }
    }

    /// <summary>
    /// A text field: a string's text, or anything else's JSON. What is typed is kept until Enter sets it; a value that
    /// changes elsewhere, a preset applied, replaces it unless it was being edited.
    /// </summary>
    private void DrawText(Row row, Setting setting)
    {
        // The value as text, made again only when the value changed; an edit not yet entered shows in its place.
        string shown = row.Shown is { } kept && Equals(setting.Value, row.Value)
            ? kept
            : setting.Value as string ?? JsonSerializer.Serialize(setting.Value, setting.Type, Json.Options);
        row.Value = setting.Value;
        row.Shown = shown;
        string text = row.Draft ?? shown;
        bool isEntered = _ui.Input(row.Id, ref text);
        row.Draft = string.Equals(text, shown, StringComparison.Ordinal) ? null : text;

        if (!isEntered) return;

        Change(setting, setting.Type == typeof(string) ? text : ParseValue(text));
        row.Draft = null;
    }

    /// <summary>
    /// Sets <paramref name="setting"/> to <paramref name="value"/>; why not is a warning.
    /// </summary>
    private void Change(Setting setting, object? value)
    {
        Result set = _settings.TrySet(setting.Key, value);
        if (!set.Ok) Debugging.Warn(set.Error);
    }

    /// <summary>
    /// Applies <paramref name="preset"/>; why not is a warning.
    /// </summary>
    private async Task ApplyAsync(Handle<Preset> preset, CancellationToken cancel)
    {
        Result applied = await _settings.ApplyAsync(preset, cancel).ConfigureAwait(false);
        if (!applied.Ok) Debugging.Warn(applied.Error);
    }

    /// <summary>
    /// <c>get Gem</c>: every setting of the gem, as <c>Gem.setting=value</c> lines, the form <c>set</c> takes; a gem
    /// with none is no gem.
    /// </summary>
    private void RunGet(ReadOnlySpan<string> words)
    {
        string gem = words.Length == 1 ? words[0] : string.Empty;
        string lines = GetLines(
            _settings.All.Where(setting => string.Equals(setting.Owner, gem, StringComparison.OrdinalIgnoreCase)));
        if (lines.Length == 0)
        {
            string gems = string.Join(
                ", ",
                _settings.All.Select(setting => setting.Owner).Distinct(StringComparer.Ordinal));
            Debugging.Warn($"get takes a gem with settings: {gems}.");

            return;
        }

        Debugging.Info(lines.TrimEnd('\n'));
    }

    /// <summary>
    /// <c>set Gem.setting=value</c>: the setting changed while running, until the next preset.
    /// </summary>
    private void RunSet(ReadOnlySpan<string> words)
    {
        if (words.Length != 1)
        {
            Debugging.Warn("set takes one Gem.setting=value, the value JSON: set DebugTools.isFrameRateShown=false.");

            return;
        }

        Result set = _settings.TrySet(words[0]);
        if (set.Ok) Debugging.Info($"Setting {words[0]}");
        else Debugging.Warn(set.Error);
    }

    /// <summary>
    /// <c>preset</c> says which preset is applied and which there are; <c>preset Name</c> applies one.
    /// </summary>
    private void RunPreset(ReadOnlySpan<string> words)
    {
        if (words.Length == 0)
        {
            int applied = Array.IndexOf(_presets, _settings.Preset);
            string current = applied < 0 ? "none" : _presetNames[applied];
            Debugging.Info($"Preset: {current}. Presets: {string.Join(", ", _presetNames)}.");

            return;
        }

        string wanted = words[0];
        int chosen = Array.FindIndex(
            _presetNames,
            name => string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase));
        if (chosen < 0)
        {
            Debugging.Warn($"There is no preset {wanted}; there are {string.Join(", ", _presetNames)}.");

            return;
        }

        ApplyAsync(_presets[chosen], _stopping).FireAndForget();
    }

    /// <summary>
    /// One setting the window offers, with everything about it that stays the same made once: where it sits in
    /// <see cref="Settings.All"/>, its tab and header and the heading of both the filter shows, its name in words, its
    /// widget's id, its tooltip, its range, an enum's values and their names in words, and a text field's text while
    /// it is being edited.
    /// </summary>
    private sealed class Row
    {
        internal required int Index { get; init; }

        internal required string Tab { get; init; }

        internal required string Header { get; init; }

        internal required string Heading { get; init; }

        internal required string Label { get; init; }

        internal required string Id { get; init; }

        internal required string Tip { get; init; }

        internal float Min { get; init; }

        internal float Max { get; init; }

        internal object[] Choices { get; init; } = [];

        internal string[] Labels { get; init; } = [];

        /// <summary>
        /// The value a text field last showed, and the text it showed it as.
        /// </summary>
        internal object? Value { get; set; }

        internal string? Shown { get; set; }

        /// <summary>
        /// What is typed in a text field and not yet entered; none while it shows the value.
        /// </summary>
        internal string? Draft { get; set; }
    }
}
