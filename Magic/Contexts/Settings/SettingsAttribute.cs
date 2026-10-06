namespace Magic.Contexts.Settings;

/// <summary>
/// Marks a class as settings: a plain pile of public read-write properties, each with a default, that a preset sets
/// by <c>"Name.Property": value</c> and <c>--set Name.Property=value</c> overrides. Any assembly can declare one (the
/// engine, a gem, a project); one object of it is made when the assembly loads, and asked for like a service, by a
/// constructor parameter of its type. In the settings window (F4) it sits under its
/// <see cref="System.ComponentModel.CategoryAttribute"/> (<c>"Tab/Header"</c>), and a property may name another;
/// <see cref="System.ComponentModel.DisplayNameAttribute"/>, <see cref="System.ComponentModel.DescriptionAttribute"/> and
/// <see cref="System.ComponentModel.DataAnnotations.RangeAttribute"/> give its label, hover text and slider range.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class SettingsAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}
