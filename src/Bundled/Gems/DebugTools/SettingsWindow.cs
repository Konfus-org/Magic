using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Magic.Contexts.Assets;
using Magic.Contexts.Settings;
using Magic.Contexts.Subscriptions;
using Magic.Contexts.Utils;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.DebugTools;

/// <summary>
/// The settings: the preset applied, which can be switched, and what the frame shows (<see cref="IGpu.DebugView"/>),
/// then a tab per gem with settings, and in it a row per setting the gem reads while running: labelled in words, its
/// doc comment shown while the mouse rests on it (<see cref="SettingsDocs"/>), a number kept to its
/// <see cref="RangeAttribute"/>. A setting marked <see cref="BrowsableAttribute"/> false is read only as its gem
/// starts, so it is not offered. An edit is set through <see cref="Settings.Set{T}"/>, live until the next preset;
/// nothing is saved. Copy gives the open tab as <c>Gem.setting=value</c> lines, the form <c>--set</c> takes. Its
/// commands: <c>get Gem</c>, <c>set Gem.setting=value</c>, <c>preset [Name]</c> and <c>view [Stage]</c>.
/// </summary>
internal sealed class SettingsWindow : IDisposable
{
    private readonly ToolWindow _window;
    private readonly IDebugUI _ui;
    private readonly Settings _settings;
    private readonly IGpu? _gpu;
    private readonly CancellationToken _stopping;
    private readonly SettingsDocs _docs;
    private readonly SubscriptionList _commands = new();
    private readonly List<Row> _rows = [];
    private readonly Dictionary<Type, (string[] Names, string[] Labels)> _enums = [];
    private readonly PassStage[] _stages = Enum.GetValues<PassStage>();

    /// <summary>
    /// Every preset there is, as the preset choice shows them, by name, and the name of each.
    /// </summary>
    private readonly Handle<Preset>[] _presets;
    private readonly string[] _presetNames;

    /// <summary>
    /// The frame as it is, then every stage in words, as the view choice shows them.
    /// </summary>
    private readonly string[] _views;

    private string[] _tabs = [];
    private int _tab;
    private bool _isCollected;

    public SettingsWindow(ToolWindow window, IDebugUI ui, Settings settings, Assets assets, IFileSystem files, ICommands? commands, IGpu? gpu, CancellationToken stopping)
    {
        _window = window;
        _ui = ui;
        _settings = settings;
        _gpu = gpu;
        _stopping = stopping;
        _docs = new SettingsDocs(files);
        _presets = [.. settings.Presets];
        _presetNames = Array.ConvertAll(_presets, preset => Path.GetFileNameWithoutExtension(assets.Path(preset)) ?? string.Empty);
        Array.Sort(_presetNames, _presets, StringComparer.OrdinalIgnoreCase);
        _views = new string[_stages.Length + 1];
        _views[0] = "Normal";
        for (int i = 0; i < _stages.Length; i++)
        {
            _views[i + 1] = Words(_stages[i].ToString());
        }

        if (commands is null)
        {
            return;
        }

        _commands.Add(commands.Register("get", RunGet));
        _commands.Add(commands.Register("set", RunSet));
        _commands.Add(commands.Register("preset", RunPreset));
        _commands.Add(commands.Register("view", RunView));
    }

    public void Dispose()
    {
        _commands.Dispose();
    }

    public void Update()
    {
        if (!_window.Begin(scrollable: true))
        {
            return;
        }

        Collect();
        DrawPreset();
        DrawView();
        if (_tabs.Length > 0)
        {
            _ui.Tabs("##gems", ref _tab, _tabs);
            _tab = Math.Clamp(_tab, 0, _tabs.Length - 1);
            DrawTab(_tabs[_tab]);
            if (_window.IsCopyClicked)
            {
                _window.Copy(TabText(_tabs[_tab]));
            }
        }
        else
        {
            _ui.Label("No gem takes settings it reads while running.");
        }

        _window.End();
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

    /// <summary>
    /// The settings a window can offer: the public ones that can be both read and set, but those marked
    /// <see cref="BrowsableAttribute"/> false, on the property or its class.
    /// </summary>
    private static bool IsOffered(PropertyInfo property)
    {
        return property.CanRead && property.CanWrite
            && property.GetCustomAttribute<BrowsableAttribute>()?.Browsable != false
            && property.DeclaringType?.GetCustomAttribute<BrowsableAttribute>()?.Browsable != false;
    }

    /// <summary>
    /// A setting's name in its key, <c>Gem.setting</c>: its name in Core's JSON dialect.
    /// </summary>
    private static string KeyOf(PropertyInfo property)
    {
        return Json.Options.PropertyNamingPolicy?.ConvertName(property.Name) ?? property.Name;
    }

    private static string JsonOf(object? value, Type type)
    {
        return JsonSerializer.Serialize(value, type, Json.Options);
    }

    /// <summary>
    /// <paramref name="gem"/>'s settings as <c>Gem.setting=value</c> lines: every one it can be given when
    /// <paramref name="isOfferedOnly"/> is false, else those the window offers.
    /// </summary>
    private static string Lines(GemSettings gem, bool isOfferedOnly)
    {
        StringBuilder text = new();
        foreach (PropertyInfo property in gem.Values.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || !property.CanWrite || (isOfferedOnly && !IsOffered(property)))
            {
                continue;
            }

            text.Append(gem.Gem).Append('.').Append(KeyOf(property)).Append('=').Append(JsonOf(property.GetValue(gem.Values), property.PropertyType)).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>
    /// The rows of every gem's settings and the tabs they make, gathered the first time the window opens, by when every
    /// gem is made.
    /// </summary>
    private void Collect()
    {
        if (_isCollected)
        {
            return;
        }

        _isCollected = true;
        List<string> tabs = [];
        foreach (GemSettings gem in _settings.Gems)
        {
            int before = _rows.Count;
            foreach (PropertyInfo property in gem.Values.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!IsOffered(property))
                {
                    continue;
                }

                string key = $"{gem.Gem}.{KeyOf(property)}";
                RangeAttribute? range = property.GetCustomAttribute<RangeAttribute>();
                _rows.Add(new Row
                {
                    Gem = gem,
                    Property = property,
                    Key = key,
                    Id = $"{Words(property.Name)}##{key}",
                    Description = _docs.SummaryOf(property),
                    Min = range is null ? 0 : Convert.ToSingle(range.Minimum, CultureInfo.InvariantCulture),
                    Max = range is null ? 0 : Convert.ToSingle(range.Maximum, CultureInfo.InvariantCulture),
                });
            }

            if (_rows.Count > before)
            {
                tabs.Add(gem.Gem);
            }
        }

        _tabs = [.. tabs];
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
    /// What the frame shows, as a choice of the frame as it is or one of its stages; with no GPU, nothing.
    /// </summary>
    private void DrawView()
    {
        if (_gpu is not { } gpu)
        {
            return;
        }

        int index = gpu.DebugView is { } stage ? Array.IndexOf(_stages, stage) + 1 : 0;
        if (_ui.Choice("View", ref index, _views))
        {
            gpu.DebugView = index == 0 ? null : _stages[index - 1];
        }

        _ui.Tooltip("Shows what a stage of the frame made in place of the finished image.");
    }

    private void DrawTab(string gem)
    {
        foreach (Row row in _rows)
        {
            if (!string.Equals(row.Gem.Gem, gem, StringComparison.Ordinal))
            {
                continue;
            }

            DrawRow(row);
            if (row.Description.Length > 0)
            {
                _ui.Tooltip(row.Description);
            }
        }
    }

    /// <summary>
    /// A row's widget for what its setting holds: a checkbox, a slider, a choice, or a text field, which is a string's
    /// text or anything else's JSON and is set when Enter is pressed in it.
    /// </summary>
    private void DrawRow(Row row)
    {
        object? current = row.Property.GetValue(row.Gem.Values);
        switch (current)
        {
            case bool flag:
                if (_ui.Checkbox(row.Id, ref flag))
                {
                    Change(row, flag);
                }

                break;
            case float number:
                if (_ui.Slider(row.Id, ref number, row.Min, row.Max))
                {
                    Change(row, number);
                }

                break;
            case double number:
                float slid = (float)number;
                if (_ui.Slider(row.Id, ref slid, row.Min, row.Max))
                {
                    Change(row, (double)slid);
                }

                break;
            case int whole:
                float slidWhole = whole;
                if (_ui.Slider(row.Id, ref slidWhole, row.Min, row.Max))
                {
                    Change(row, (int)MathF.Round(slidWhole));
                }

                break;
            case Enum choice:
                (string[] names, string[] labels) = EnumNames(row.Property.PropertyType);
                int index = Array.IndexOf(names, choice.ToString());
                if (_ui.Choice(row.Id, ref index, labels) && index >= 0)
                {
                    Change(row, Enum.Parse(row.Property.PropertyType, names[index]));
                }

                break;
            default:
                DrawText(row, current);
                break;
        }
    }

    /// <summary>
    /// A text field: a string's text, or anything else's JSON. What is typed is kept until Enter sets it; a value that
    /// changes elsewhere, a preset applied, replaces it unless it was being edited.
    /// </summary>
    private void DrawText(Row row, object? current)
    {
        if (!Equals(current, row.Value) || row.Shown is null)
        {
            string shown = current as string ?? JsonOf(current, row.Property.PropertyType);
            if (row.Draft is null || string.Equals(row.Draft, row.Shown, StringComparison.Ordinal))
            {
                row.Draft = shown;
            }

            row.Value = current;
            row.Shown = shown;
        }

        string draft = row.Draft ?? string.Empty;
        bool isEntered = _ui.Input(row.Id, ref draft);
        row.Draft = draft;
        if (!isEntered)
        {
            return;
        }

        if (row.Property.PropertyType == typeof(string))
        {
            Change(row, draft);
        }
        else
        {
            SetLine($"{row.Key}={draft}");
        }

        row.Shown = null;
        row.Draft = null;
    }

    /// <summary>
    /// Sets <paramref name="row"/>'s setting to <paramref name="value"/>; why not is a warning.
    /// </summary>
    private void Change(Row row, object value)
    {
        Result set = _settings.Set(row.Key, value);
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
        Result<Setting> parsed = Settings.Parse(line);
        Result set = parsed.Ok ? _settings.Set(parsed.Value.Key, parsed.Value.Value) : Result.Failure(parsed.Error);
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
    /// The open tab's settings as <c>Gem.setting=value</c> lines.
    /// </summary>
    private string TabText(string gem)
    {
        return Find(gem) is { } settings ? Lines(settings, isOfferedOnly: true) : string.Empty;
    }

    private GemSettings? Find(string gem)
    {
        foreach (GemSettings settings in _settings.Gems)
        {
            if (string.Equals(settings.Gem, gem, StringComparison.OrdinalIgnoreCase))
            {
                return settings;
            }
        }

        return null;
    }

    /// <summary>
    /// <c>get Gem</c>: every setting of the gem, as <c>Gem.setting=value</c> lines, the form <c>set</c> takes.
    /// </summary>
    private void RunGet(ReadOnlySpan<string> words)
    {
        if (words.Length != 1 || Find(words[0]) is not { } gem)
        {
            Debugging.Warn($"get takes a gem with settings: {GemNames()}.");
            return;
        }

        Debugging.Info(Lines(gem, isOfferedOnly: false).TrimEnd('\n'));
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
            gpu.DebugView = null;
            Debugging.Info("View: the frame as it is.");
            return;
        }

        if (!Enum.TryParse(words[0], ignoreCase: true, out PassStage stage) || !Enum.IsDefined(stage))
        {
            Debugging.Warn($"view takes one of the frame's stages: {string.Join(", ", Enum.GetNames<PassStage>())}.");
            return;
        }

        gpu.DebugView = stage;
        Debugging.Info($"View: {Words(stage.ToString())}.");
    }

    private string GemNames()
    {
        StringBuilder names = new();
        foreach (GemSettings gem in _settings.Gems)
        {
            names.Append(names.Length == 0 ? string.Empty : ", ").Append(gem.Gem);
        }

        return names.ToString();
    }

    /// <summary>
    /// One setting the window offers: whose it is, its property, its key, its widget's id, what its doc comment says,
    /// its range, and a text field's text while it is being edited.
    /// </summary>
    private sealed class Row
    {
        public required GemSettings Gem { get; init; }

        public required PropertyInfo Property { get; init; }

        public required string Key { get; init; }

        public required string Id { get; init; }

        public required string Description { get; init; }

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
