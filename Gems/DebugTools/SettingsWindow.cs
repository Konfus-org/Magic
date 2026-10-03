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
    private readonly (string Name, PropertyInfo Section, PropertyInfo[] Values)[] _sections;
    private readonly Dictionary<Type, string[]> _enumNames = [];

    public SettingsWindow(Settings settings, IInput? input, IClipboard? clipboard) : base("Settings", Key.F4, input, clipboard)
    {
        _settings = settings;
        _sections = [.. typeof(Settings).GetProperties()
            .Select(section => (section.Name, section, section.PropertyType.GetProperties().Where(property => property.CanRead && property.CanWrite).ToArray()))];
    }

    protected override void Draw(in Frame frame)
    {
        if (!Open)
            return;

        Begin(scrollable: true);
        foreach ((string name, PropertyInfo sectionProperty, PropertyInfo[] values) in _sections)
        {
            object? section = sectionProperty.GetValue(_settings);
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
        foreach ((string name, PropertyInfo sectionProperty, PropertyInfo[] values) in _sections)
        {
            object? section = sectionProperty.GetValue(_settings);
            if (section is null)
                continue;

            foreach (PropertyInfo value in values)
                text.Append(name).Append('.').Append(value.Name).Append(' ').Append(value.GetValue(section)).AppendLine();
        }

        return text.ToString();
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
