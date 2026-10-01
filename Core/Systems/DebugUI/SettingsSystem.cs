using Magic.Contexts;
using Magic.Contexts.Input;
using Magic.Contexts.Settings;
using Magic.Utils;
using System.Drawing;
using System.Reflection;

namespace Magic.Systems.DebugUI;

/// <summary>
/// The settings window, opened with F4: the project's <see cref="Settings"/>, one section per property, edited in
/// place. bool, enum, float and size values are edited live and the systems that read them pick the change up the next
/// frame; a string is only read at start-up by whoever uses it, so it is shown as text.
/// </summary>
public sealed class SettingsSystem : DebugWindowSystem
{
    private readonly Settings _settings;
    private readonly (string Name, PropertyInfo Section, PropertyInfo[] Values)[] _sections;
    private readonly Dictionary<Type, string[]> _enumNames = [];

    public SettingsSystem(Settings settings) : base(Key.F4)
    {
        _settings = settings;
        _sections = [.. typeof(Settings).GetProperties()
            .Select(section => (section.Name, section, section.PropertyType.GetProperties().Where(p => p.CanRead && p.CanWrite).ToArray()))];
    }

    protected override void Run(in Frame frame)
    {
        if (!Open)
            return;

        Debugging.UI.Begin("Settings", scrollable: true);
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

    private void DrawSetting(object section, PropertyInfo property)
    {
        Type type = property.PropertyType;
        if (type == typeof(bool))
        {
            bool value = (bool)property.GetValue(section)!;
            if (Debugging.UI.Checkbox(property.Name, ref value))
                property.SetValue(section, value);
        }
        else if (type == typeof(float))
        {
            float value = (float)property.GetValue(section)!;
            if (Debugging.UI.Slider(property.Name, ref value))
                property.SetValue(section, value);
        }
        else if (type == typeof(Size))
        {
            Size value = (Size)property.GetValue(section)!;
            float width = value.Width, height = value.Height;
            bool changed = Debugging.UI.Slider($"{property.Name}.Width", ref width);
            changed |= Debugging.UI.Slider($"{property.Name}.Height", ref height);
            if (changed)
                property.SetValue(section, new Size(Math.Max(0, (int)MathF.Round(width)), Math.Max(0, (int)MathF.Round(height))));
        }
        else if (type.IsEnum)
        {
            string[] names = EnumNames(type);
            int index = Array.IndexOf(names, property.GetValue(section)!.ToString());
            if (Debugging.UI.Choice(property.Name, ref index, names))
                property.SetValue(section, Enum.Parse(type, names[index]));
        }
        else if (type == typeof(string))
        {
            // Read once at start-up by whoever uses it, so there is nothing to change live: shown, not edited.
            Debugging.UI.Text($"{property.Name}: {(string?)property.GetValue(section) ?? "default"} (set in the project file)");
        }
    }

    private string[] EnumNames(Type type)
    {
        if (!_enumNames.TryGetValue(type, out string[]? names))
            _enumNames[type] = names = Enum.GetNames(type);

        return names;
    }
}
