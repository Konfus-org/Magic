using Magic.Contexts;
using Magic.Contexts.Input;
using Magic.Contexts.Settings;
using Magic.Interfaces;
using Magic.Utils;
using System.Drawing;
using System.Reflection;
using System.Text;

namespace DebugToolsGem;

/// <summary>
/// The settings window, opened with F4: the project's <see cref="Settings"/>, one section per property, edited in
/// place. bool, enum, float and size values are edited live and the systems that read them pick the change up the next
/// frame; a string is only read at start-up by whoever uses it, so it is shown as text.
/// </summary>
internal sealed class SettingsWindow : Window
{
    private readonly Settings _settings;
    private readonly List<(string Name, Func<object?> Section, PropertyInfo[] Values)> _sections = [];
    private readonly Dictionary<Type, string[]> _enumNames = [];

    public SettingsWindow(Settings settings, IInput? input, IClipboard? clipboard) : base("Settings", Key.F4, input, clipboard)
    {
        _settings = settings;
        foreach (PropertyInfo section in typeof(Settings).GetProperties())
            AddSection(section.Name, () => section.GetValue(_settings), section.PropertyType);
    }

    protected override void Draw(in Frame frame)
    {
        if (!Open)
            return;

        Begin(scrollable: true);
        foreach ((string name, Func<object?> sectionOf, PropertyInfo[] values) in _sections)
        {
            object? section = sectionOf();
            if (section is null)
                continue;

            Debugging.UI.Text(name);
            foreach (PropertyInfo value in values)
                DrawSetting(section, value);
        }
        Debugging.UI.End();
    }

    protected override string Contents()
    {
        StringBuilder text = new();
        foreach ((string name, Func<object?> sectionOf, PropertyInfo[] values) in _sections)
        {
            object? section = sectionOf();
            if (section is null)
                continue;

            foreach (PropertyInfo value in values)
                text.Append(name).Append('.').Append(value.Name).Append(' ').Append(value.GetValue(section)).AppendLine();
        }

        return text.ToString();
    }

    /// <summary>
    /// A section is a settings class: its plain properties are edited under its name, and a property that is itself a
    /// settings class is a section of its own under a dotted name (Render.Shadows).
    /// </summary>
    private void AddSection(string name, Func<object?> sectionOf, Type type)
    {
        PropertyInfo[] properties = type.GetProperties().Where(property => property.CanRead && property.CanWrite).ToArray();
        _sections.Add((name, sectionOf, [.. properties.Where(property => !IsSection(property.PropertyType))]));
        foreach (PropertyInfo nested in properties.Where(property => IsSection(property.PropertyType)))
            AddSection($"{name}.{nested.Name}", () => sectionOf() is { } section ? nested.GetValue(section) : null, nested.PropertyType);
    }

    private static bool IsSection(Type type)
    {
        return type.IsClass && type != typeof(string);
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

    private string[] EnumNames(Type type)
    {
        if (!_enumNames.TryGetValue(type, out string[]? names))
            _enumNames[type] = names = Enum.GetNames(type);

        return names;
    }
}
