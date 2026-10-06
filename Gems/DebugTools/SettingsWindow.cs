using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Input;
using Magic.Interfaces;
using Magic.Services;
using Magic.Utils;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace DebugToolsGem;

/// <summary>
/// The settings window, opened with F4: the preset in use, which can be switched, then a tab per group (Display,
/// World, Lighting, ...), a header per subgroup under it, a row per value. The rows are the settings objects
/// (<see cref="Settings.All"/>), wherever their <see cref="CategoryAttribute"/> puts them, and, when a renderer exports
/// its pipeline (<see cref="IPipelineTuning"/>), the passes' parameters, wherever their <see cref="Pass.Group"/> and
/// <see cref="ParamTuning"/> put them. Each is labelled in words, described on hover and kept to its range. A value
/// edited here is set through <see cref="Settings.Set"/>, as the <c>set</c> cheat sets one: live, until the next preset
/// is applied; nothing is saved. The window's Copy gives the open
/// tab's values as <c>Owner.Property=value</c> lines, and the console's <c>get Owner</c> the same for one owner: the
/// form the <c>set</c> cheat and <c>--set</c> take.
/// </summary>
internal sealed class SettingsWindow : Window
{
    /// <summary>
    /// The tab what says nothing about where it goes is put in.
    /// </summary>
    private const string General = "General";

    /// <summary>
    /// Kept last, whatever comes first: what is there to check the engine, not to tune the game.
    /// </summary>
    private const string Debug = "Debug";

    private readonly Settings _settings;
    private readonly Assets _assets;
    private readonly IPipelineTuning? _pipeline;
    private readonly List<Row> _rows = [];
    private readonly List<string> _tabNames = [];
    private readonly Dictionary<Type, (string[] Names, string[] Labels)> _enumNames = [];
    private readonly IDisposable[] _commands;
    private string[] _tabs = [];
    private int _tab;

    public SettingsWindow(Settings settings, Assets assets, IPipelineTuning? pipeline, IInput? input, IClipboard? clipboard) : base("Settings", Key.F4, input, clipboard)
    {
        _settings = settings;
        _assets = assets;
        _pipeline = pipeline;
        _commands =
        [
            Debugging.Commands.Register("get", Get),
        ];
    }

    public override void Dispose()
    {
        foreach (IDisposable command in _commands)
            command.Dispose();

        base.Dispose();
    }

    protected override void Draw(in Frame frame)
    {
        if (!Open)
            return;

        Collect();
        Begin(scrollable: true);
        DrawPreset();
        Debugging.UI.Tabs("##settings", ref _tab, _tabs);
        if (_tab < _tabs.Length)
            DrawTab(_tabs[_tab]);

        Debugging.UI.End();
    }

    protected override string Contents()
    {
        Collect();
        if (_tab >= _tabs.Length)
            return "";

        StringBuilder text = new();
        string? header = null;
        foreach (Row row in _rows)
        {
            if (row.Tab != _tabs[_tab] || row.Key.Length == 0)
                continue;

            if (row.Header != header)
                text.Append("# ").Append(_tabs[_tab]).Append(" / ").Append(header = row.Header).AppendLine();

            text.Append(row.Key).Append('=').Append(Format(row)).AppendLine();
        }

        return text.ToString();
    }

    /// <summary>
    /// A name in words: <c>LodBias</c> and <c>lodBias</c> are "Lod bias"; an acronym stays as it is (<c>HdrToLDR</c> is
    /// "Hdr to LDR").
    /// </summary>
    private static string Words(string name)
    {
        StringBuilder words = new(name.Length + 8);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            bool afterLower = i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]));
            bool endsAcronym = i > 0 && char.IsUpper(name[i - 1]) && i + 1 < name.Length && char.IsLower(name[i + 1]);
            if (i == 0)
            {
                words.Append(char.ToUpperInvariant(c));
            }
            else if (char.IsUpper(c) && (afterLower || endsAcronym))
            {
                bool acronym = i + 1 < name.Length && char.IsUpper(name[i + 1]);
                words.Append(' ').Append(acronym ? c : char.ToLowerInvariant(c));
            }
            else
            {
                words.Append(c);
            }
        }

        return words.ToString();
    }

    /// <summary>
    /// <c>"Tab/Header"</c> split; a group with no header goes under <paramref name="header"/>, and no group at all
    /// under <see cref="General"/>.
    /// </summary>
    private static (string Tab, string Header) Split(string? group, string header)
    {
        if (string.IsNullOrWhiteSpace(group))
            return (General, header);

        int slash = group.IndexOf('/', StringComparison.Ordinal);
        return slash < 0 ? (group.Trim(), header) : (group[..slash].Trim(), group[(slash + 1)..].Trim());
    }

    /// <summary>
    /// The values a slider, checkbox or choice can edit, the budgets (a slider each), and strings, which are shown.
    /// </summary>
    private static bool IsShown(Type type)
    {
        return type == typeof(bool) || type == typeof(float) || type == typeof(int) || type.IsEnum || type == typeof(string) || type == typeof(Dictionary<string, int>);
    }

    /// <summary>
    /// Every row, sections first and then the pipeline's passes, and the tabs they make: in the order first seen,
    /// <see cref="Debug"/> last. Gathered again every frame the window is open, so a gem loaded or a pipeline changed
    /// shows at once.
    /// </summary>
    private void Collect()
    {
        _rows.Clear();
        foreach (object settings in _settings.All)
            AddSection(Settings.NameOf(settings), settings);
        foreach (TunablePass pass in _pipeline?.Passes ?? [])
            AddPass(pass);

        _tabNames.Clear();
        foreach (Row row in _rows)
        {
            if (!_tabNames.Contains(row.Tab))
                _tabNames.Add(row.Tab);
        }

        if (_tabNames.Remove(Debug))
            _tabNames.Add(Debug);

        if (_tabNames.SequenceEqual(_tabs))
            return;

        _tabs = [.. _tabNames];
        _tab = Math.Clamp(_tab, 0, Math.Max(0, _tabs.Length - 1));
    }

    private void AddSection(string name, object section)
    {
        Type type = section.GetType();
        string? sectionGroup = type.GetCustomAttribute<CategoryAttribute>()?.Category;
        foreach (PropertyInfo property in type.GetProperties())
        {
            if (!property.CanRead || !property.CanWrite || !IsShown(property.PropertyType))
                continue;

            (string tab, string header) = Split(property.GetCustomAttribute<CategoryAttribute>()?.Category ?? sectionGroup, Words(name));
            RangeAttribute? range = property.GetCustomAttribute<RangeAttribute>();
            _rows.Add(new Row
            {
                Tab = tab,
                Header = header,
                Label = property.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName ?? Words(property.Name),
                Description = property.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "",
                Key = $"{name}.{property.Name}",
                Min = range is null ? 0f : Convert.ToSingle(range.Minimum, System.Globalization.CultureInfo.InvariantCulture),
                Max = range is null ? 0f : Convert.ToSingle(range.Maximum, System.Globalization.CultureInfo.InvariantCulture),
                Section = section,
                Property = property,
            });
        }
    }

    /// <summary>
    /// A row per parameter, and a line for a pass that is disabled, under the pass's own group.
    /// </summary>
    private void AddPass(TunablePass pass)
    {
        string name = Path.GetFileNameWithoutExtension(pass.Name);
        if (pass.Error is not null)
        {
            (string tab, string header) = Split(pass.Group, name);
            _rows.Add(new Row { Tab = tab, Header = header, Label = $"{name} is disabled: {pass.Error}", Description = "", Key = "" });
        }

        foreach (TunableParam parameter in pass.Params)
        {
            (string tab, string header) = Split(parameter.Tuning.Group, name);
            _rows.Add(new Row
            {
                Tab = tab,
                Header = header,
                Label = parameter.Tuning.Label.Length > 0 ? parameter.Tuning.Label : Words(parameter.Name),
                Description = parameter.Tuning.Description,
                Key = $"{name}.{parameter.Name}",
                Min = parameter.Tuning.Min,
                Max = parameter.Tuning.Max,
                Pass = pass.Id,
                Param = parameter,
            });
        }
    }

    /// <summary>
    /// The tab's headers in the order first seen, each with its rows under it while it is open.
    /// </summary>
    private void DrawTab(string tab)
    {
        string? header = null;
        bool open = false;
        foreach (Row row in _rows)
        {
            if (row.Tab != tab)
                continue;

            if (row.Header != header)
            {
                header = row.Header;
                open = Debugging.UI.Header($"{header}##{tab}");
            }

            if (open)
                DrawRow(row);
        }
    }

    private void DrawRow(Row row)
    {
        if (row.Property is { } property && row.Section is { } section)
            DrawSetting(row, section, property);
        else if (row.Param is { } parameter)
            DrawParam(row, parameter);
        else
            Debugging.UI.Text(row.Label);

        if (row.Description.Length > 0)
            Debugging.UI.Tooltip(row.Description);
    }

    private void DrawSetting(Row row, object section, PropertyInfo property)
    {
        string label = $"{row.Label}##{row.Key}";
        object? current = property.GetValue(section);
        if (current is bool flag)
        {
            if (Debugging.UI.Checkbox(label, ref flag))
                Edit(row, flag, typeof(bool));
        }
        else if (current is float number)
        {
            if (Debugging.UI.Slider(label, ref number, row.Min, row.Max))
                Edit(row, number, typeof(float));
        }
        else if (current is int whole)
        {
            float slid = whole;
            if (Debugging.UI.Slider(label, ref slid, row.Min, row.Max))
                Edit(row, (int)MathF.Round(slid), typeof(int));
        }
        else if (current is Enum choice)
        {
            (string[] names, string[] labels) = EnumNames(property.PropertyType);
            int index = Array.IndexOf(names, choice.ToString());
            if (Debugging.UI.Choice(label, ref index, labels))
                Edit(row, Enum.Parse(property.PropertyType, names[index]), property.PropertyType);
        }
        else if (current is Dictionary<string, int> entries)
        {
            foreach ((string key, int value) in entries)
            {
                float slid = value;
                if (Debugging.UI.Slider($"{key}##{row.Key}.{key}", ref slid, 0f, 65536f))
                    Edit(row, new Dictionary<string, int>(entries) { [key] = Math.Max(0, (int)MathF.Round(slid)) }, property.PropertyType);
            }
        }
        else if (property.PropertyType == typeof(string))
        {
            string text = (string?)current ?? "";
            if (Debugging.UI.Input(label, ref text))
                Edit(row, text, typeof(string));
        }
    }

    /// <summary>
    /// The preset in use, as a choice of every <c>.preset</c> asset; choosing one applies it.
    /// </summary>
    private void DrawPreset()
    {
        string[] presets = _assets.Paths(".preset");
        int index = Array.IndexOf(presets, _settings.Preset?.Path);
        if (!Debugging.UI.Choice("Preset", ref index, presets) || index < 0)
            return;

        if (_assets.Load(_assets.Find<Preset>(presets[index])) is { } preset)
            _settings.Apply(preset);
    }

    private void DrawParam(Row row, in TunableParam parameter)
    {
        if (parameter.Tuning.Fixed)
        {
            Debugging.UI.Text($"{row.Label}: {Format(parameter)}");
            return;
        }

        Param value = parameter.Value;
        string label = $"{row.Label}##{row.Key}{row.Pass}";
        switch (parameter.Kind)
        {
            case TunableKind.Bool:
                bool flag = value.X != 0f;
                if (Debugging.UI.Checkbox(label, ref flag))
                    Edit(row, flag ? "true" : "false");
                break;
            case TunableKind.Int or TunableKind.Uint:
                float whole = value.X;
                if (Debugging.UI.Slider(label, ref whole, row.Min, row.Max))
                    Edit(row, MathF.Round(parameter.Kind == TunableKind.Uint ? MathF.Max(0f, whole) : whole), typeof(float));
                break;
            case TunableKind.Color:
                float r = value.R, g = value.G, b = value.B, a = value.A;
                bool tinted = Debugging.UI.Slider($"{row.Label} red##{row.Key}{row.Pass}", ref r, 0f, 1f);
                tinted |= Debugging.UI.Slider($"{row.Label} green##{row.Key}{row.Pass}", ref g, 0f, 1f);
                tinted |= Debugging.UI.Slider($"{row.Label} blue##{row.Key}{row.Pass}", ref b, 0f, 1f);
                tinted |= Debugging.UI.Slider($"{row.Label} alpha##{row.Key}{row.Pass}", ref a, 0f, 1f);
                if (tinted)
                    Edit(row, new[] { r, g, b, a }, typeof(float[]));
                break;
            default:
                int components = parameter.Kind switch { TunableKind.Float2 => 2, TunableKind.Float3 => 3, TunableKind.Float4 => 4, _ => 1 };
                float x = value.X, y = value.Y, z = value.Z, w = value.W;
                bool changed = Debugging.UI.Slider(components == 1 ? label : $"{row.Label} x##{row.Key}{row.Pass}", ref x, row.Min, row.Max);
                if (components > 1)
                    changed |= Debugging.UI.Slider($"{row.Label} y##{row.Key}{row.Pass}", ref y, row.Min, row.Max);
                if (components > 2)
                    changed |= Debugging.UI.Slider($"{row.Label} z##{row.Key}{row.Pass}", ref z, row.Min, row.Max);
                if (components > 3)
                    changed |= Debugging.UI.Slider($"{row.Label} w##{row.Key}{row.Pass}", ref w, row.Min, row.Max);
                if (changed && components == 1)
                    Edit(row, x, typeof(float));
                else if (changed)
                    Edit(row, new[] { x, y, z, w }[..components], typeof(float[]));
                break;
        }
    }

    /// <summary>
    /// The row's new value, set through the settings as the <c>set</c> cheat sets it: live, until the next preset.
    /// </summary>
    private void Edit(Row row, object value, Type type)
    {
        Edit(row, Format(value, type));
    }

    private void Edit(Row row, string json)
    {
        _settings.Set([$"{row.Key}={json}"]);
    }

    /// <summary>
    /// A row's value as <c>set</c> and <c>--set</c> take it: JSON, on one line.
    /// </summary>
    private static string Format(Row row)
    {
        if (row.Property is { } property && row.Section is { } section)
            return Format(property.GetValue(section), property.PropertyType);

        return row.Param is { } parameter ? Format(parameter) : "";
    }

    private static string Format(object? value, Type type)
    {
        return JsonSerializer.Serialize(JsonSerializer.SerializeToElement(value, type, AssetJson.Options));
    }

    private static string Format(in TunableParam parameter)
    {
        Param value = parameter.Value;
        return parameter.Kind switch
        {
            TunableKind.Float2 => Format(new[] { value.X, value.Y }, typeof(float[])),
            TunableKind.Float3 => Format(new[] { value.X, value.Y, value.Z }, typeof(float[])),
            TunableKind.Float4 => Format(new[] { value.X, value.Y, value.Z, value.W }, typeof(float[])),
            TunableKind.Color => Format(new[] { value.R, value.G, value.B, value.A }, typeof(float[])),
            TunableKind.Bool => value.X != 0f ? "true" : "false",
            _ => Format(value.X, typeof(float)),
        };
    }

    /// <summary>
    /// <c>get Owner</c>: every value of a settings object or a pass, as <c>Owner.Property=value</c> lines, the form
    /// <c>set</c> takes.
    /// </summary>
    private void Get(string[] args)
    {
        if (args.Length != 1)
        {
            Debugging.Log.Warn($"Usage: get <Owner>, one of {string.Join(", ", _settings.All.Select(Settings.NameOf))} or a pass's name");
            return;
        }

        StringBuilder text = new();
        if (SettingsNamed(args[0]) is { } settings)
        {
            foreach (PropertyInfo property in settings.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(property => property.CanRead && property.CanWrite))
                text.Append(Settings.NameOf(settings)).Append('.').Append(property.Name).Append('=').Append(Format(property.GetValue(settings), property.PropertyType)).AppendLine();
        }
        else if (PassNamed(args[0]) is { } pass)
        {
            foreach (TunableParam parameter in pass.Params)
                text.Append(Path.GetFileNameWithoutExtension(pass.Name)).Append('.').Append(parameter.Name).Append('=').Append(Format(parameter)).AppendLine();
        }
        else
        {
            Debugging.Log.Warn($"{args[0]} names no settings and no pass in the pipeline.");
            return;
        }

        Debugging.Log.Info(text.ToString().TrimEnd());
    }

    private object? SettingsNamed(string name)
    {
        return _settings.All.FirstOrDefault(settings => string.Equals(Settings.NameOf(settings), name, StringComparison.OrdinalIgnoreCase));
    }

    private TunablePass? PassNamed(string name)
    {
        return _pipeline?.Passes.FirstOrDefault(pass => string.Equals(Path.GetFileNameWithoutExtension(pass.Name), name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The enum's names, and the same in words for the choice to show (<c>SunShadow</c> is "Sun shadow").
    /// </summary>
    private (string[] Names, string[] Labels) EnumNames(Type type)
    {
        if (!_enumNames.TryGetValue(type, out (string[] Names, string[] Labels) names))
        {
            string[] raw = Enum.GetNames(type);
            _enumNames[type] = names = (raw, [.. raw.Select(Words)]);
        }

        return names;
    }

    /// <summary>
    /// One row of a tab: a settings property (<see cref="Section"/> and <see cref="Property"/>), a pass parameter
    /// (<see cref="Pass"/> and <see cref="Param"/>), or neither, a line of text. <see cref="Key"/> is what Copy prints
    /// it as; empty for a line.
    /// </summary>
    private sealed class Row
    {
        public required string Tab { get; init; }

        public required string Header { get; init; }

        public required string Label { get; init; }

        public required string Description { get; init; }

        public required string Key { get; init; }

        public float Min { get; init; }

        public float Max { get; init; }

        public object? Section { get; init; }

        public PropertyInfo? Property { get; init; }

        public ulong Pass { get; init; }

        public TunableParam? Param { get; init; }
    }
}
