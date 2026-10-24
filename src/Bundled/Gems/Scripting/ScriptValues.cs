using System.Reflection;
using System.Text.Json;
using Magic.Contexts.ECS;
using Magic.Contexts.Utils;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.Scripting;

/// <summary>
/// A script as JSON, saved with the world and put back on a new copy of its class: its values, the public properties
/// a prefab sets, by the name a file gives them, and the other fields it kept for itself by name, but those that are no
/// data of its own, a service its constructor was handed or something made rather than kept. A member that could not be
/// saved or put back is warned about once. Main thread.
/// </summary>
internal sealed class ScriptValues(IServiceProvider services)
{
    /// <summary>
    /// The members of scripts that could not be saved or put back, as <c>Class.member</c>, each warned about once.
    /// </summary>
    private readonly HashSet<string> _unkept = new(StringComparer.Ordinal);

    /// <summary>
    /// <paramref name="script"/> as it is now, as JSON by name, to save: its values (<see cref="GetValues"/>) as a file
    /// names them, and its other fields by name, but those that are no data of its own (<see cref="IsHanded"/>) and
    /// those JSON cannot hold, warned about once.
    /// </summary>
    internal Dictionary<string, JsonElement> GetState(IScript script)
    {
        Type type = script.GetType();
        Dictionary<string, JsonElement> values = new(StringComparer.Ordinal);
        HashSet<string> backing = new(StringComparer.Ordinal);

        // Its values first, each one's backing field noted so the fields below do not save it twice.
        foreach (PropertyInfo property in GetValues(type))
        {
            backing.Add($"<{property.Name}>k__BackingField");
            Write(values, type, GetKey(property), property.PropertyType, property.GetValue(script));
        }

        foreach (FieldInfo field in type.GetFields(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (!backing.Contains(field.Name) && !IsHanded(field.FieldType))
                Write(values, type, GetSavedName(field), field.FieldType, field.GetValue(script));
        }

        return values;
    }

    /// <summary>
    /// Sets each value <paramref name="state"/> saved back on <paramref name="script"/>, a new copy of its class, on its
    /// property or field; one its class no longer has, or that no longer fits, is warned about once and left. The
    /// values set back on its properties, by the property's name, as a prefab gives them.
    /// </summary>
    internal Dictionary<string, object?> Restore(IScript script, ScriptState state)
    {
        Type type = script.GetType();
        Dictionary<string, object?> values = new(StringComparer.Ordinal);
        foreach ((string name, JsonElement json) in state.Values)
        {
            if (FindValue(type, name) is { } property)
            {
                if (TryRead(type, name, json, property.PropertyType, out object? value))
                {
                    property.SetValue(script, value);
                    values[property.Name] = value;
                }
            }
            else if (FindField(type, name) is { } field)
            {
                if (TryRead(type, name, json, field.FieldType, out object? value)) field.SetValue(script, value);
            }
            else
            {
                WarnOnce(
                    type,
                    name,
                    $"Script {type.Name}'s saved {name} is not put back, as its class no longer has it.");
            }
        }

        return values;
    }

    /// <summary>
    /// <paramref name="type"/>'s public properties with a getter and a setter: its values, which a prefab sets.
    /// </summary>
    private static List<PropertyInfo> GetValues(Type type)
    {
        List<PropertyInfo> values = [];
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetMethod is { IsPublic: true } && property.SetMethod is { IsPublic: true }
                && property.GetIndexParameters().Length == 0)
            {
                values.Add(property);
            }
        }

        return values;
    }

    /// <summary>
    /// <paramref name="type"/>'s public property with a getter and a setter that a file names <paramref name="key"/>,
    /// in Core's dialect: one of its values. Null when it has none.
    /// </summary>
    private static PropertyInfo? FindValue(Type type, string key)
    {
        foreach (PropertyInfo property in GetValues(type))
        {
            if (string.Equals(GetKey(property), key, StringComparison.Ordinal)) return property;
        }

        return null;
    }

    /// <summary>
    /// <paramref name="type"/>'s field saved as <paramref name="name"/> (<see cref="GetSavedName"/>): its own of that
    /// name, else the one the compiler made for a property or a primary constructor parameter of it. Null when it has
    /// none.
    /// </summary>
    private static FieldInfo? FindField(Type type, string name)
    {
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        return type.GetField(name, Any)
            ?? type.GetField($"<{name}>k__BackingField", Any) ?? type.GetField($"<{name}>P", Any);
    }

    /// <summary>
    /// A value's key as a file gives it, in Core's dialect.
    /// </summary>
    private static string GetKey(PropertyInfo property) => JsonNamingPolicy.CamelCase.ConvertName(property.Name);

    /// <summary>
    /// A field's name as it is saved: its own, or for one the compiler made, the name of the property or primary
    /// constructor parameter it is for, which is between the angle brackets.
    /// </summary>
    private static string GetSavedName(FieldInfo field)
    {
        int end = field.Name.IndexOf('>', StringComparison.Ordinal);

        return field.Name.StartsWith('<') && end > 1 ? field.Name[1..end] : field.Name;
    }

    /// <summary>
    /// Whether a field of <paramref name="type"/> is no data of the script's own: a service its constructor was handed,
    /// or something made rather than kept, a delegate, a task or a disposable, which a copy put back makes again.
    /// </summary>
    private bool IsHanded(Type type)
    {
        return typeof(Delegate).IsAssignableFrom(type) || typeof(Task).IsAssignableFrom(type)
            || typeof(IDisposable).IsAssignableFrom(type)
            || (!type.IsValueType && services.GetService(type) is not null);
    }

    /// <summary>
    /// Writes <paramref name="value"/>, a <paramref name="type"/>'s member <paramref name="name"/> of
    /// <paramref name="valueType"/>, into <paramref name="values"/> as JSON; a value JSON cannot hold is warned about
    /// once and left out.
    /// </summary>
    private void Write(Dictionary<string, JsonElement> values, Type type, string name, Type valueType, object? value)
    {
        Result<JsonElement> json = Json.Write(value, valueType);
        if (json.Ok)
        {
            values[name] = json.Value;
        }
        else
        {
            WarnOnce(
                type,
                name,
                $"Script {type.Name}'s {name} is not saved, so a copy put back starts over without it: {json.Error}");
        }
    }

    /// <summary>
    /// Reads <paramref name="json"/>, a saved value of <paramref name="type"/>'s member <paramref name="name"/>, as
    /// <paramref name="valueType"/>; false, warned about once, when it no longer fits.
    /// </summary>
    private bool TryRead(Type type, string name, JsonElement json, Type valueType, out object? value)
    {
        if (json.ValueKind == JsonValueKind.Null)
        {
            // A reference the script had none of: what it is put back as.
            value = null;

            return true;
        }

        Result<object> read = Json.Read(json, valueType);
        if (read.Ok)
        {
            value = read.Value;

            return true;
        }

        WarnOnce(type, name, $"Script {type.Name}'s saved {name} is not put back, as it no longer fits: {read.Error}");
        value = null;

        return false;
    }

    /// <summary>
    /// Warns <paramref name="message"/> once for <paramref name="type"/>'s member <paramref name="name"/>, which could
    /// not be saved or put back, so a world saved or put back again says it no more.
    /// </summary>
    private void WarnOnce(Type type, string name, string message)
    {
        if (_unkept.Add($"{type.Name}.{name}")) Debugging.Warn(message);
    }
}
