using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Input;
using Magic.Contexts.Settings;
using Magic.Interfaces;
using Magic.Utils;
using System.Drawing;
using System.Reflection;
using System.Text;

namespace DebugToolsGem;

/// <summary>
/// The settings window, opened with F4, one tab per group. First the project's <see cref="Settings"/>, a tab per section
/// (Render, Assets, Streaming): bool, enum, float and size values are edited live and the systems that read them pick
/// the change up the next frame; a string is only read at start-up, so it is shown as text. Then, when a renderer
/// exports its pipeline (<see cref="IPipelineTuning"/>), a tab per stage with the passes that have parameters (or a
/// problem), edited live in memory. The window's Copy gives the open tab's values as text.
/// </summary>
internal sealed class SettingsWindow : Window
{
    private readonly Settings _settings;
    private readonly IPipelineTuning? _pipeline;
    private readonly List<(string Name, PropertyInfo Section, PropertyInfo[] Values)> _sections = [];
    private readonly List<string> _stages = [];
    private readonly Dictionary<Type, string[]> _enumNames = [];
    private string[] _tabs = [];
    private int _tab;

    public SettingsWindow(Settings settings, IPipelineTuning? pipeline, IInput? input, IClipboard? clipboard) : base("Settings", Key.F4, input, clipboard)
    {
        _settings = settings;
        _pipeline = pipeline;
        foreach (PropertyInfo section in typeof(Settings).GetProperties())
        {
            PropertyInfo[] values = [.. section.PropertyType.GetProperties().Where(property => property.CanRead && property.CanWrite && IsEditable(property.PropertyType))];
            _sections.Add((section.Name, section, values));
        }
    }

    protected override void Draw(in Frame frame)
    {
        if (!Open)
            return;

        IReadOnlyList<TunablePass> passes = _pipeline?.Passes ?? [];
        UpdateTabs(passes);
        Begin(scrollable: true);
        Debugging.UI.Tabs("##settings", ref _tab, _tabs);
        if (_tab < _sections.Count)
            DrawSection(_sections[_tab]);
        else if (_tab < _tabs.Length)
            DrawStage(passes, _tabs[_tab]);

        Debugging.UI.End();
    }

    protected override string Contents()
    {
        StringBuilder text = new();
        if (_tab < _sections.Count)
        {
            (string name, PropertyInfo sectionProperty, PropertyInfo[] values) = _sections[_tab];
            if (sectionProperty.GetValue(_settings) is { } section)
            {
                foreach (PropertyInfo value in values)
                    text.Append(name).Append('.').Append(value.Name).Append(' ').Append(value.GetValue(section)).AppendLine();
            }
        }
        else if (_tab < _tabs.Length && _pipeline is { } pipeline)
        {
            foreach (TunablePass pass in pipeline.Passes)
            {
                if (pass.Stage != _tabs[_tab])
                    continue;

                foreach (TunableParam parameter in pass.Params)
                    text.Append(Path.GetFileNameWithoutExtension(pass.Name)).Append('.').Append(parameter.Name).Append(' ').Append(Format(parameter)).AppendLine();
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// The settings a slider, checkbox or choice can edit, and strings, which are shown. Collections are left out.
    /// </summary>
    private static bool IsEditable(Type type)
    {
        return type == typeof(bool) || type == typeof(float) || type == typeof(int) || type == typeof(Size) || type.IsEnum || type == typeof(string);
    }

    /// <summary>
    /// The tabs: the settings sections, then every stage that has a pass worth showing, in pipeline order. Rebuilt only
    /// when the stages change.
    /// </summary>
    private void UpdateTabs(IReadOnlyList<TunablePass> passes)
    {
        _stages.Clear();
        foreach (TunablePass pass in passes)
        {
            if (Shows(pass) && !_stages.Contains(pass.Stage))
                _stages.Add(pass.Stage);
        }

        if (SameStages())
            return;

        _tabs = [.. _sections.Select(section => section.Name), .. _stages];
        _tab = Math.Min(_tab, _tabs.Length - 1);
    }

    private bool SameStages()
    {
        if (_tabs.Length != _sections.Count + _stages.Count)
            return false;

        for (int i = 0; i < _stages.Count; i++)
        {
            if (_tabs[_sections.Count + i] != _stages[i])
                return false;
        }

        return true;
    }

    private static bool Shows(TunablePass pass)
    {
        return pass.Params.Count > 0 || pass.Error is not null;
    }

    private void DrawSection((string Name, PropertyInfo Section, PropertyInfo[] Values) section)
    {
        if (section.Section.GetValue(_settings) is not { } values)
            return;

        foreach (PropertyInfo value in section.Values)
            DrawSetting(values, value);
    }

    private void DrawSetting(object section, PropertyInfo property)
    {
        Type type = property.PropertyType;
        object? current = property.GetValue(section);
        if (current is bool flag)
        {
            if (Debugging.UI.Checkbox(property.Name, ref flag))
                property.SetValue(section, flag);
        }
        else if (current is float number)
        {
            if (Debugging.UI.Slider(property.Name, ref number))
                property.SetValue(section, number);
        }
        else if (current is int whole)
        {
            float slid = whole;
            if (Debugging.UI.Slider(property.Name, ref slid))
                property.SetValue(section, (int)MathF.Round(slid));
        }
        else if (current is Size size)
        {
            float width = size.Width, height = size.Height;
            bool changed = Debugging.UI.Slider($"{property.Name}.Width", ref width);
            changed |= Debugging.UI.Slider($"{property.Name}.Height", ref height);
            if (changed)
                property.SetValue(section, new Size(Math.Max(0, (int)MathF.Round(width)), Math.Max(0, (int)MathF.Round(height))));
        }
        else if (current is Enum choice)
        {
            string[] names = EnumNames(type);
            int index = Array.IndexOf(names, choice.ToString());
            if (Debugging.UI.Choice(property.Name, ref index, names))
                property.SetValue(section, Enum.Parse(type, names[index]));
        }
        else if (type == typeof(string))
        {
            // Read once at start-up by whoever uses it, so there is nothing to change live: shown, not edited.
            Debugging.UI.Text($"{property.Name}: {(string?)current ?? "default"} (set in the project file)");
        }
    }

    /// <summary>
    /// A stage's passes: each its name (and problem) and a control per parameter.
    /// </summary>
    private void DrawStage(IReadOnlyList<TunablePass> passes, string stage)
    {
        foreach (TunablePass pass in passes)
        {
            if (pass.Stage != stage || !Shows(pass))
                continue;

            string name = Path.GetFileNameWithoutExtension(pass.Name);
            Debugging.UI.Text(pass.Error is null ? name : $"{name} (disabled: {pass.Error})");
            foreach (TunableParam parameter in pass.Params)
                DrawParam(pass.Id, parameter);
        }
    }

    private void DrawParam(ulong pass, in TunableParam parameter)
    {
        Param value = parameter.Value;
        string label = $"{parameter.Name}##{pass}";
        switch (parameter.Kind)
        {
            case TunableKind.Bool:
                bool flag = value.X != 0f;
                if (Debugging.UI.Checkbox(label, ref flag))
                    _pipeline?.Set(pass, parameter.Name, Param.Of(flag));
                break;
            case TunableKind.Int or TunableKind.Uint:
                float whole = value.X;
                if (Debugging.UI.Slider(label, ref whole))
                    _pipeline?.Set(pass, parameter.Name, Param.Of(MathF.Round(parameter.Kind == TunableKind.Uint ? MathF.Max(0f, whole) : whole)));
                break;
            case TunableKind.Color:
                float r = value.R, g = value.G, b = value.B, a = value.A;
                bool tinted = Debugging.UI.Slider($"{parameter.Name}.r##{pass}", ref r, 0f, 1f);
                tinted |= Debugging.UI.Slider($"{parameter.Name}.g##{pass}", ref g, 0f, 1f);
                tinted |= Debugging.UI.Slider($"{parameter.Name}.b##{pass}", ref b, 0f, 1f);
                tinted |= Debugging.UI.Slider($"{parameter.Name}.a##{pass}", ref a, 0f, 1f);
                if (tinted)
                    _pipeline?.Set(pass, parameter.Name, new Param { R = r, G = g, B = b, A = a });
                break;
            default:
                int components = parameter.Kind switch { TunableKind.Float2 => 2, TunableKind.Float3 => 3, TunableKind.Float4 => 4, _ => 1 };
                float x = value.X, y = value.Y, z = value.Z, w = value.W;
                bool changed = components == 1 ? Debugging.UI.Slider(label, ref x) : Debugging.UI.Slider($"{parameter.Name}.x##{pass}", ref x);
                if (components > 1)
                    changed |= Debugging.UI.Slider($"{parameter.Name}.y##{pass}", ref y);
                if (components > 2)
                    changed |= Debugging.UI.Slider($"{parameter.Name}.z##{pass}", ref z);
                if (components > 3)
                    changed |= Debugging.UI.Slider($"{parameter.Name}.w##{pass}", ref w);
                if (changed)
                    _pipeline?.Set(pass, parameter.Name, new Param { X = x, Y = y, Z = z, W = w });
                break;
        }
    }

    private static string Format(in TunableParam parameter)
    {
        Param value = parameter.Value;
        return parameter.Kind switch
        {
            TunableKind.Float2 => $"{value.X} {value.Y}",
            TunableKind.Float3 => $"{value.X} {value.Y} {value.Z}",
            TunableKind.Float4 => $"{value.X} {value.Y} {value.Z} {value.W}",
            TunableKind.Color => $"{value.R} {value.G} {value.B} {value.A}",
            TunableKind.Bool => value.X != 0f ? "true" : "false",
            _ => $"{value.X}",
        };
    }

    private string[] EnumNames(Type type)
    {
        if (!_enumNames.TryGetValue(type, out string[]? names))
            _enumNames[type] = names = Enum.GetNames(type);

        return names;
    }
}
