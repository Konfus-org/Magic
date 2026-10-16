using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Magic.Contexts.Assets;
using Magic.Contexts.Input;
using Magic.Contexts.Settings;
using Magic.Contexts.Subscriptions;
using Magic.Contexts.Utils;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.DebugTools;

/// <summary>
/// The settings: the preset applied, which can be switched, and what the frame shows (<see cref="IGpu.DebugView"/>),
/// which F5 and F6 step through whether or not the window is open, and which says its name and what it shows as a
/// line on screen while it is a debug view; a filter, which while it has text shows every setting it finds in place
/// of the tabs; then a tab per category of settings (<see cref="CategoryAttribute"/> up to its slash, else the gem's name), in it a
/// header per sub-category (past the slash, else the gem's name), which a click folds away, and under it a row per
/// setting (<see cref="Settings.All"/>): labelled in words, its key and description shown while the mouse rests on it,
/// a number kept to its <see cref="RangeAttribute"/>. A setting marked <see cref="RequireRestartAttribute"/> says on
/// its row that a change starts the engine again. An edit is set through <see cref="Settings.Set{T}"/>, live until the next preset;
/// nothing is saved. Copy gives the open tab, or what the filter finds, as <c>Gem.setting=value</c> lines, the form
/// <c>--set</c> takes. Its commands: <c>get Gem</c>, <c>set Gem.setting=value</c>, <c>preset [Name]</c> and
/// <c>view [Name]</c>.
/// </summary>
internal sealed class SettingsWindow : IDisposable
{
    /// <summary>
    /// Where the debug views are: a pass each, named for what it shows, which whatever draws the frame runs by name
    /// (<see cref="IGpu.DebugView"/>).
    /// </summary>
    private const string ViewsFolder = "Passes/Debug";

    /// <summary>
    /// What the view choice says of the frame as it is.
    /// </summary>
    private const string NormalTip = "The frame as it is. A debug view shows one thing the frame made, its depth or its normals, say, in its place.";

    private readonly ToolWindow _window;
    private readonly IDebugUI _ui;
    private readonly Settings _settings;
    private readonly IGpu? _gpu;
    private readonly IInput? _input;
    private readonly CancellationToken _stopping;
    private readonly SubscriptionList _commands = new();
    private readonly List<Row> _rows = [];

    /// <summary>
    /// How many headers each tab has: a tab with one shows none.
    /// </summary>
    private readonly Dictionary<string, int> _headerCounts = new(StringComparer.Ordinal);

    private readonly Dictionary<Type, (string[] Names, string[] Labels)> _enums = [];
    /// <summary>
    /// The debug views there are, by name: the passes in <c>Passes/Debug</c>, each named for what it shows, and what
    /// each says it shows, filled as the passes load.
    /// </summary>
    private readonly string[] _viewNames;
    private readonly string[] _viewDescriptions;

    /// <summary>
    /// Every preset there is, as the preset choice shows them, by name, and the name of each.
    /// </summary>
    private readonly Handle<Preset>[] _presets;
    private readonly string[] _presetNames;

    /// <summary>
    /// The frame as it is, then every debug view in words, as the view choice shows them.
    /// </summary>
    private readonly string[] _views;

    /// <summary>
    /// The line on screen for the view shown, made when the view changes.
    /// </summary>
    private string _viewLine = string.Empty;
    private string _linedView = string.Empty;

    private string[] _tabs = [];
    private int _tab;

    /// <summary>
    /// What is typed in the filter: while there is any, the rows it finds show in place of the tabs.
    /// </summary>
    private string _filter = string.Empty;

    public SettingsWindow(ToolWindow window, IDebugUI ui, Settings settings, Assets assets, ICommands? commands, IGpu? gpu, IInput? input, CancellationToken stopping)
    {
        _window = window;
        _ui = ui;
        _settings = settings;
        _gpu = gpu;
        _input = input;
        _stopping = stopping;
        _presets = [.. settings.Presets];
        _presetNames = Array.ConvertAll(_presets, preset => Path.GetFileNameWithoutExtension(assets.Path(preset)) ?? string.Empty);
        Array.Sort(_presetNames, _presets, StringComparer.OrdinalIgnoreCase);
        Handle<Pass>[] passes = Array.ConvertAll([.. assets.Paths(ViewsFolder)], assets.Find<Pass>);
        _viewNames = Array.ConvertAll(passes, pass => Path.GetFileNameWithoutExtension(assets.Path(pass)) ?? string.Empty);
        Array.Sort(_viewNames, passes, StringComparer.OrdinalIgnoreCase);
        _viewDescriptions = new string[_viewNames.Length];
        Array.Fill(_viewDescriptions, string.Empty);
        _views = new string[_viewNames.Length + 1];
        _views[0] = "Normal";
        for (int i = 0; i < _viewNames.Length; i++)
        {
            _views[i + 1] = Words(_viewNames[i]);
        }

        DescribeViewsAsync(assets, passes, stopping).FireAndForget();
        if (commands is null)
        {
            return;
        }

        _commands.Add(commands.Register("get", "Logs every setting of a gem as Gem.setting=value lines: get DebugTools.", RunGet));
        _commands.Add(commands.Register("set", "Changes a setting while running, until the next preset: set DebugTools.frameRate=false.", RunSet));
        _commands.Add(commands.Register("preset", "Logs the preset applied and every preset there is; with a name, applies that preset.", RunPreset));
        _commands.Add(commands.Register("view", "Shows a debug view, a pass in Passes/Debug by its file name, in place of the finished image; alone, the frame as it is.", RunView));
    }

    public void Dispose()
    {
        _commands.Dispose();
    }

    public void Update()
    {
        StepView();
        ShowView();
        if (!_window.Begin(scrollable: true))
        {
            return;
        }

        IReadOnlyList<Setting> all = _settings.All;
        Collect(all);

        DrawPreset();
        DrawView();
        _ui.Input("##filter", ref _filter, hint: "Filter", focus: true);
        _ui.Tooltip("Every setting, in any tab, whose name, key or description has this.");

        if (_tabs.Length == 0)
        {
            _ui.Label("No gem takes settings.");
        }
        else if (_filter.Length > 0)
        {
            DrawFiltered(all);
            if (_window.IsCopyClicked)
            {
                _window.Copy(Lines(Matches));
            }
        }
        else
        {
            _ui.Tabs("##gems", ref _tab, _tabs);
            _tab = Math.Clamp(_tab, 0, _tabs.Length - 1);
            DrawTab(_tabs[_tab], all);
            if (_window.IsCopyClicked)
            {
                string tab = _tabs[_tab];
                _window.Copy(Lines(setting => string.Equals(CategoryOf(setting), tab, StringComparison.Ordinal)));
            }
        }

        _window.End();
    }

    /// <summary>
    /// Whether the filter finds <paramref name="setting"/>: in its name in words, its key or its description, in any
    /// case.
    /// </summary>
    private bool Matches(Setting setting)
    {
        return Words(setting.Name).Contains(_filter, StringComparison.OrdinalIgnoreCase)
            || setting.Key.Contains(_filter, StringComparison.OrdinalIgnoreCase)
            || setting.Description.Contains(_filter, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The rows of every tab the filter finds, under a header per tab and sub-category, which a click folds away, in
    /// place of the tabs.
    /// </summary>
    private void DrawFiltered(IReadOnlyList<Setting> all)
    {
        string header = string.Empty;
        bool isUnfolded = true;
        foreach (Row row in _rows)
        {
            Setting setting = all[row.Index];
            if (!Matches(setting))
            {
                continue;
            }

            string under = string.Equals(row.Tab, row.Header, StringComparison.Ordinal) ? row.Tab : $"{row.Tab}: {row.Header}";
            if (!string.Equals(under, header, StringComparison.Ordinal))
            {
                header = under;
                isUnfolded = _ui.Header(header);
            }

            if (!isUnfolded)
            {
                continue;
            }

            DrawRow(row, setting);
            _ui.Tooltip(row.Tip);
        }
    }

    /// <summary>
    /// A name in words: <c>RenderScale</c> and <c>renderScale</c> are "Render scale"; an acronym stays as it is
    /// (<c>LodBiasLOD</c> is "Lod bias LOD").
    /// </summary>
    private static string Words(string name)
    {
        StringBuilder words = new(name.Length + 8);
        for (int i = 0; i < name.Length; i++)
        {
            char character = name[i];
            bool isAfterLower = i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]));
            bool isEndOfAcronym = i > 0 && char.IsUpper(name[i - 1]) && i + 1 < name.Length && char.IsLower(name[i + 1]);
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

    private static bool IsOwnedBy(Setting setting, string gem)
    {
        return string.Equals(setting.Owner, gem, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The tab a setting is under: its category up to its slash, <c>Render</c> of <c>Render/Shadows</c>, or its gem's
    /// name without one.
    /// </summary>
    private static string CategoryOf(Setting setting)
    {
        string? category = setting.Attributes.OfType<CategoryAttribute>().FirstOrDefault()?.Category;
        int slash = category?.IndexOf('/', StringComparison.Ordinal) ?? -1;
        return category is null ? setting.Owner : slash < 0 ? category : category[..slash];
    }

    /// <summary>
    /// The header a setting is under in its tab: its category past its slash, <c>Shadows</c> of <c>Render/Shadows</c>,
    /// or its gem's name without one.
    /// </summary>
    private static string HeaderOf(Setting setting)
    {
        string? category = setting.Attributes.OfType<CategoryAttribute>().FirstOrDefault()?.Category;
        int slash = category?.IndexOf('/', StringComparison.Ordinal) ?? -1;
        return category is null || slash < 0 ? setting.Owner : category[(slash + 1)..];
    }

    private static string JsonOf(object? value, Type type)
    {
        return JsonSerializer.Serialize(value, type, Json.Options);
    }

    /// <summary>
    /// What is typed for a setting, as the console takes it: JSON when it parses as JSON (1920, true, ["a"],
    /// "quoted"), and a string when it does not.
    /// </summary>
    private static JsonNode? ValueOf(string text)
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
    /// The settings <paramref name="includes"/> as <c>Gem.setting=value</c> lines.
    /// </summary>
    private string Lines(Predicate<Setting> includes)
    {
        StringBuilder text = new();
        foreach (Setting setting in _settings.All)
        {
            if (includes(setting))
            {
                text.Append(setting.Key).Append('=').Append(JsonOf(setting.Value, setting.Type)).Append('\n');
            }
        }

        return text.ToString();
    }

    private bool HasGem(string gem)
    {
        return _settings.All.Any(setting => IsOwnedBy(setting, gem));
    }

    /// <summary>
    /// The rows of every gem's live settings and the tabs they make, gathered the first time the window opens, by when
    /// every gem is made, so once there are rows there is nothing to gather. A row keeps where its setting sits in
    /// <paramref name="all"/>, whose order is fixed. Tabs come in the order they are first met, and within a tab a
    /// header's rows are kept together, the headers in the order they are first met too.
    /// </summary>
    private void Collect(IReadOnlyList<Setting> all)
    {
        if (_rows.Count > 0)
        {
            return;
        }

        List<Row> rows = [];
        List<string> tabs = [];
        List<(string Tab, string Header)> headers = [];
        for (int i = 0; i < all.Count; i++)
        {
            Setting setting = all[i];
            RangeAttribute? range = setting.Attributes.OfType<RangeAttribute>().FirstOrDefault();
            bool restarts = setting.Attributes.OfType<RequireRestartAttribute>().Any();
            string tip = setting.Description.Length > 0 ? $"{setting.Key}: {setting.Description}" : setting.Key;
            Row row = new()
            {
                Index = i,
                Tab = CategoryOf(setting),
                Header = HeaderOf(setting),
                Label = Words(setting.Name),
                Id = $"{Words(setting.Name)}##{setting.Key}",
                Tip = restarts ? $"{tip} Changed, the engine starts again at once, every setting as it is: the world is saved and put back, so the game goes on where it was." : tip,
                Min = range is null ? 0 : Convert.ToSingle(range.Minimum, CultureInfo.InvariantCulture),
                Max = range is null ? 0 : Convert.ToSingle(range.Maximum, CultureInfo.InvariantCulture),
            };
            rows.Add(row);
            if (!tabs.Contains(row.Tab, StringComparer.Ordinal))
            {
                tabs.Add(row.Tab);
            }

            if (!headers.Contains((row.Tab, row.Header)))
            {
                headers.Add((row.Tab, row.Header));
            }
        }

        string[] met = [.. tabs];
        tabs.Sort((left, right) => TabOrder(left, met).CompareTo(TabOrder(right, met)));
        _rows.AddRange(rows.OrderBy(row => tabs.IndexOf(row.Tab)).ThenBy(row => headers.IndexOf((row.Tab, row.Header))));
        _tabs = [.. tabs];
        foreach ((string tab, _) in headers)
        {
            _headerCounts[tab] = _headerCounts.GetValueOrDefault(tab) + 1;
        }
    }

    /// <summary>
    /// Where a tab goes: Render first and Debug last, the rest between them as they were first met.
    /// </summary>
    private static int TabOrder(string tab, string[] met)
    {
        return string.Equals(tab, "Render", StringComparison.Ordinal) ? -1
            : string.Equals(tab, "Debug", StringComparison.Ordinal) ? met.Length
            : Array.IndexOf(met, tab);
    }

    /// <summary>
    /// The preset applied, as a choice of every preset there is; choosing one applies it.
    /// </summary>
    private void DrawPreset()
    {
        int index = Array.IndexOf(_presets, _settings.Preset);
        if (_ui.Choice("Preset", ref index, _presetNames) && index >= 0)
        {
            ApplyAsync(_presets[index], _stopping).FireAndForget();
        }
    }

    /// <summary>
    /// What the frame shows, as a choice of the frame as it is or one of the debug views, with what the one chosen
    /// shows as its tooltip; with no GPU, nothing.
    /// </summary>
    private void DrawView()
    {
        if (_gpu is not { } gpu)
        {
            return;
        }

        int index = ViewIndex(gpu);
        if (_ui.Choice("View", ref index, _views))
        {
            gpu.DebugView = index == 0 ? string.Empty : _viewNames[index - 1];
        }

        _ui.Tooltip(index == 0 || _viewDescriptions[index - 1].Length == 0 ? NormalTip : _viewDescriptions[index - 1]);
    }

    /// <summary>
    /// Where the view shown is in the choice: 0 for the frame as it is, or a view not listed.
    /// </summary>
    private int ViewIndex(IGpu gpu)
    {
        return Array.FindIndex(_viewNames, name => string.Equals(name, gpu.DebugView, StringComparison.OrdinalIgnoreCase)) + 1;
    }

    /// <summary>
    /// F5 shows the next view and F6 the one before, round through the frame as it is, whether or not the window is
    /// open.
    /// </summary>
    private void StepView()
    {
        if (_gpu is not { } gpu || _input is not { } input)
        {
            return;
        }

        int step = input[Control.KeyF5].WasPressed ? 1 : input[Control.KeyF6].WasPressed ? -1 : 0;
        if (step == 0)
        {
            return;
        }

        int index = (ViewIndex(gpu) + step + _views.Length) % _views.Length;
        gpu.DebugView = index == 0 ? string.Empty : _viewNames[index - 1];
    }

    /// <summary>
    /// While a debug view is shown, a line on screen under the frame rate saying which, and what it shows, so a frame
    /// of black or a flat colour reads, and a screenshot says what it is of.
    /// </summary>
    private void ShowView()
    {
        if (_gpu is not { } gpu || gpu.DebugView.Length == 0)
        {
            return;
        }

        int index = ViewIndex(gpu);
        string description = index == 0 ? string.Empty : _viewDescriptions[index - 1];
        if (!string.Equals(_linedView, gpu.DebugView, StringComparison.Ordinal) || !_viewLine.EndsWith(description, StringComparison.Ordinal))
        {
            _linedView = gpu.DebugView;
            string name = index == 0 ? gpu.DebugView : _views[index];
            _viewLine = description.Length == 0 ? $"View: {name}" : $"View: {name}. {description}";
        }

        _ui.Text(_viewLine);
    }

    /// <summary>
    /// Loads each view's pass for what it says it shows; one that does not load has no description, which the renderer
    /// says why when it is shown.
    /// </summary>
    private async Task DescribeViewsAsync(Assets assets, Handle<Pass>[] passes, CancellationToken cancel)
    {
        for (int i = 0; i < passes.Length; i++)
        {
            Result<Pass> pass = await assets.LoadAsync(passes[i], cancel).ConfigureAwait(false);
            if (pass.Ok)
            {
                _viewDescriptions[i] = pass.Value.Description;
            }
        }
    }

    /// <summary>
    /// <paramref name="tab"/>'s rows, under a header per sub-category or gem, which a click folds away; no header
    /// when the tab has only one.
    /// </summary>
    private void DrawTab(string tab, IReadOnlyList<Setting> all)
    {
        bool hasHeaders = _headerCounts.GetValueOrDefault(tab) > 1;
        string header = string.Empty;
        bool isUnfolded = true;
        foreach (Row row in _rows)
        {
            if (!string.Equals(row.Tab, tab, StringComparison.Ordinal))
            {
                continue;
            }

            if (hasHeaders && !string.Equals(row.Header, header, StringComparison.Ordinal))
            {
                header = row.Header;
                isUnfolded = _ui.Header(header);
            }

            if (!isUnfolded)
            {
                continue;
            }

            DrawRow(row, all[row.Index]);
            _ui.Tooltip(row.Tip);
        }
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
                if (_ui.Checkbox(row.Id, ref flag))
                {
                    Change(setting, flag);
                }

                break;
            case float number:
                if (_ui.Slider(row.Id, ref number, row.Min, row.Max))
                {
                    Change(setting, number);
                }

                break;
            case double number:
                float slid = (float)number;
                if (_ui.Slider(row.Id, ref slid, row.Min, row.Max))
                {
                    Change(setting, (double)slid);
                }

                break;
            case int whole:
                float slidWhole = whole;
                if (_ui.Slider(row.Id, ref slidWhole, row.Min, row.Max))
                {
                    Change(setting, (int)MathF.Round(slidWhole));
                }

                break;
            case Enum choice:
                (string[] names, string[] labels) = EnumNames(setting.Type);
                int index = Array.IndexOf(names, choice.ToString());
                if (_ui.Choice(row.Id, ref index, labels) && index >= 0)
                {
                    Change(setting, Enum.Parse(setting.Type, names[index]));
                }

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
        if (!Equals(setting.Value, row.Value) || row.Shown is null)
        {
            string shown = setting.Value as string ?? JsonOf(setting.Value, setting.Type);
            if (row.Draft is null || string.Equals(row.Draft, row.Shown, StringComparison.Ordinal))
            {
                row.Draft = shown;
            }

            row.Value = setting.Value;
            row.Shown = shown;
        }

        string draft = row.Draft ?? string.Empty;
        bool isEntered = _ui.Input(row.Id, ref draft);
        row.Draft = draft;
        if (!isEntered)
        {
            return;
        }

        Change(setting, setting.Type == typeof(string) ? draft : ValueOf(draft));
        row.Shown = null;
        row.Draft = null;
    }

    /// <summary>
    /// Sets <paramref name="setting"/> to <paramref name="value"/>; why not is a warning.
    /// </summary>
    private void Change(Setting setting, object? value)
    {
        Result set = _settings.Set(setting.Key, value);
        if (!set.Ok)
        {
            Debugging.Warn(set.Error);
        }
    }

    /// <summary>
    /// Sets the setting <c>Gem.setting=value</c> names, the value JSON, or a string when it is not; why not is a
    /// warning.
    /// </summary>
    private bool SetLine(string line)
    {
        int equals = line.IndexOf('=', StringComparison.Ordinal);
        Result set = equals < 0
            ? Result.Failure($"A setting is Gem.setting=value; '{line}' is not one.")
            : _settings.Set(line[..equals], ValueOf(line[(equals + 1)..]));
        if (!set.Ok)
        {
            Debugging.Warn(set.Error);
        }

        return set.Ok;
    }

    /// <summary>
    /// Applies <paramref name="preset"/>; why not is a warning.
    /// </summary>
    private async Task ApplyAsync(Handle<Preset> preset, CancellationToken cancel)
    {
        Result applied = await _settings.ApplyAsync(preset, cancel).ConfigureAwait(false);
        if (!applied.Ok)
        {
            Debugging.Warn(applied.Error);
        }
    }

    /// <summary>
    /// The enum's names, and the same in words for its choice to show.
    /// </summary>
    private (string[] Names, string[] Labels) EnumNames(Type type)
    {
        if (!_enums.TryGetValue(type, out (string[] Names, string[] Labels) names))
        {
            string[] raw = Enum.GetNames(type);
            string[] labels = new string[raw.Length];
            for (int i = 0; i < raw.Length; i++)
            {
                labels[i] = Words(raw[i]);
            }

            names = (raw, labels);
            _enums[type] = names;
        }

        return names;
    }

    /// <summary>
    /// <c>get Gem</c>: every setting of the gem, as <c>Gem.setting=value</c> lines, the form <c>set</c> takes.
    /// </summary>
    private void RunGet(ReadOnlySpan<string> words)
    {
        if (words.Length != 1 || !HasGem(words[0]))
        {
            Debugging.Warn($"get takes a gem with settings: {GemNames()}.");
            return;
        }

        string gem = words[0];
        Debugging.Info(Lines(setting => IsOwnedBy(setting, gem)).TrimEnd('\n'));
    }

    /// <summary>
    /// <c>set Gem.setting=value</c>: the setting changed while running, until the next preset.
    /// </summary>
    private void RunSet(ReadOnlySpan<string> words)
    {
        if (words.Length != 1)
        {
            Debugging.Warn("set takes one Gem.setting=value, the value JSON: set ImGuiDebugUI.fontSize=24.");
            return;
        }

        if (SetLine(words[0]))
        {
            Debugging.Info($"Setting {words[0]}");
        }
    }

    /// <summary>
    /// <c>preset</c> says which preset is applied and which there are; <c>preset Name</c> applies one.
    /// </summary>
    private void RunPreset(ReadOnlySpan<string> words)
    {
        if (words.Length == 0)
        {
            int applied = Array.IndexOf(_presets, _settings.Preset);
            Debugging.Info($"Preset: {(applied < 0 ? "none" : _presetNames[applied])}. Presets: {string.Join(", ", _presetNames)}.");
            return;
        }

        string wanted = words[0];
        int chosen = Array.FindIndex(_presetNames, name => string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase));
        if (chosen < 0)
        {
            Debugging.Warn($"There is no preset {wanted}; there are {string.Join(", ", _presetNames)}.");
            return;
        }

        ApplyAsync(_presets[chosen], _stopping).FireAndForget();
    }

    /// <summary>
    /// <c>view Stage</c> shows what that stage of the frame made in place of the finished image; <c>view</c> alone shows
    /// the frame as it is again.
    /// </summary>
    private void RunView(ReadOnlySpan<string> words)
    {
        if (_gpu is not { } gpu)
        {
            Debugging.Warn("There is no GPU whose frame to view.");
            return;
        }

        if (words.Length == 0)
        {
            gpu.DebugView = string.Empty;
            Debugging.Info("View: the frame as it is.");
            return;
        }

        string wanted = words[0];
        string? view = Array.Find(_viewNames, name => string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase));
        if (view is null)
        {
            Debugging.Warn($"view takes one of the debug views: {string.Join(", ", _viewNames)}.");
            return;
        }

        gpu.DebugView = view;
        Debugging.Info($"View: {Words(view)}.");
    }

    private string GemNames()
    {
        return string.Join(", ", _settings.All.Select(setting => setting.Owner).Distinct(StringComparer.Ordinal));
    }

    /// <summary>
    /// One setting the window offers: where it sits in <see cref="Settings.All"/>, its tab and header, its widget's
    /// id, its tooltip, its range, and a text field's text while it is being edited.
    /// </summary>
    private sealed class Row
    {
        public required int Index { get; init; }

        public required string Tab { get; init; }

        public required string Header { get; init; }

        /// <summary>
        /// Whether its gem reads it while running, so it can be edited; else its value is shown alone.
        /// </summary>

        public required string Label { get; init; }

        public required string Id { get; init; }

        public required string Tip { get; init; }

        public float Min { get; init; }

        public float Max { get; init; }

        /// <summary>
        /// The value a text field last showed, and the text it showed it as.
        /// </summary>
        public object? Value { get; set; }

        public string? Shown { get; set; }

        /// <summary>
        /// What is in a text field, typed or shown.
        /// </summary>
        public string? Draft { get; set; }
    }
}
