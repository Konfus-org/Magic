using System.Reflection;
using System.Text.Json;
using Magic.Debugging;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Files;
using Magic.Utils;

namespace Magic.CSharpScripting;

/// <summary>
/// The state of a copy of a script as JSON by name, both ways: out of a copy for a save, and into a copy that a save
/// made. It is the values that a prefab sets, by the name a file gives them, and the other fields that the copy keeps
/// for itself, by name.
/// </summary>
/// <remarks>
/// A field that is no data of the copy's own is left out, as a copy gets it again when it is made: its entity, a
/// service of a gem, a delegate, a task or a disposable. A member that JSON cannot hold, or that the class no longer
/// has, goes to the log one time. Game thread.
/// </remarks>
internal sealed class ScriptState(IServiceProvider services)
{
    private const BindingFlags AnyField = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    /// <summary>
    /// The members that did not go into a save or come back out of one, as <c>Class.member</c>, each logged one time.
    /// </summary>
    private readonly HashSet<string> _logged = new(StringComparer.Ordinal);

    /// <summary>
    /// The state of <paramref name="script"/> as it is now, for a save.
    /// </summary>
    public List<KeyValuePair<string, JsonElement>> Read(IScript script)
    {
        Type type = script.GetType();
        List<KeyValuePair<string, JsonElement>> state = [];

        // The values first, each one's backing field noted, so the fields below do not write it a second time.
        HashSet<string> backing = new(StringComparer.Ordinal);
        foreach (PropertyInfo value in GetValues(type))
        {
            backing.Add($"<{value.Name}>k__BackingField");
            Read(state, type, GetKey(value), value.PropertyType, value.GetValue(script));
        }

        foreach (FieldInfo field in type.GetFields(AnyField))
        {
            if (!backing.Contains(field.Name) && !IsHanded(field.FieldType))
                Read(state, type, GetFieldName(field), field.FieldType, field.GetValue(script));
        }

        return state;
    }

    /// <summary>
    /// Sets each value of <paramref name="values"/> on the member of <paramref name="script"/> of that name: the
    /// public settable property, whatever the case of its first letter, else the field.
    /// </summary>
    /// <remarks>
    /// A value that the script has no such member for, or that the member's type cannot hold, goes to the log and is
    /// left out, so the rest of the script still runs.
    /// </remarks>
    public void Write(ScriptValues values, IScript script, Entity entity)
    {
        Type type = script.GetType();
        foreach ((string name, JsonElement value) in values.All)
        {
            if (FindValue(type, name) is { } property)
            {
                if (TryRead(type, name, value, property.PropertyType, out object? held))
                    property.SetValue(script, held);
            }
            else if (FindField(type, name) is { } field)
            {
                if (TryRead(type, name, value, field.FieldType, out object? held))
                    field.SetValue(script, held);
            }
            else
            {
                LogOnce(type, name, $"Script {type.Name} on {entity} has no settable {name}, so {value} is left out.");
            }
        }
    }

    /// <summary>
    /// The values of <paramref name="type"/>: its public properties that have both a getter and a setter, which a
    /// prefab sets.
    /// </summary>
    private static List<PropertyInfo> GetValues(Type type)
    {
        List<PropertyInfo> values = [];
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (
                property.GetMethod is { IsPublic: true }
                && property.SetMethod is { IsPublic: true }
                && property.GetIndexParameters().Length == 0
            )
            {
                values.Add(property);
            }
        }

        return values;
    }

    /// <summary>
    /// The value of <paramref name="type"/> that a file names <paramref name="name"/>, or null when it has none.
    /// </summary>
    private static PropertyInfo? FindValue(Type type, string name) =>
        GetValues(type).Find(value => value.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The field of <paramref name="type"/> saved as <paramref name="name"/>: its own of that name, else the one that
    /// the compiler made for a property or a primary constructor parameter of that name. Null when it has none.
    /// </summary>
    private static FieldInfo? FindField(Type type, string name) =>
        type.GetField(name, AnyField)
        ?? type.GetField($"<{name}>k__BackingField", AnyField)
        ?? type.GetField($"<{name}>P", AnyField);

    /// <summary>
    /// The name that a file gives a value, as the prefab that sets it writes it.
    /// </summary>
    private static string GetKey(PropertyInfo value) => JsonNamingPolicy.CamelCase.ConvertName(value.Name);

    /// <summary>
    /// The name that a field is saved as: its own, or for one that the compiler made, the name between the angle
    /// brackets, which is that of the property or primary constructor parameter it holds.
    /// </summary>
    private static string GetFieldName(FieldInfo field)
    {
        int end = field.Name.IndexOf('>', StringComparison.Ordinal);

        return field.Name.StartsWith('<') && end > 1 ? field.Name[1..end] : field.Name;
    }

    /// <summary>
    /// Whether a field of <paramref name="type"/> is no data of the copy's own, so a save leaves it out: its entity, a
    /// service that a gem gives, a delegate, a task or a disposable, which a copy gets again when it is made.
    /// </summary>
    private bool IsHanded(Type type) =>
        type == typeof(Entity)
        || typeof(Delegate).IsAssignableFrom(type)
        || typeof(Task).IsAssignableFrom(type)
        || typeof(IDisposable).IsAssignableFrom(type)
        || (!type.IsValueType && services.GetService(type) is not null);

    /// <summary>
    /// Writes <paramref name="value"/>, the member <paramref name="name"/> of <paramref name="type"/>, into
    /// <paramref name="state"/> as JSON; one that JSON cannot hold goes to the log one time and is left out.
    /// </summary>
    private void Read(
        List<KeyValuePair<string, JsonElement>> state,
        Type type,
        string name,
        Type memberType,
        object? value
    )
    {
        Result<JsonElement> json = Json.Write(value, memberType);
        if (json.Ok)
        {
            state.Add(KeyValuePair.Create(name, json.Value));

            return;
        }

        LogOnce(
            type,
            name,
            $"Script {type.Name}'s {name} is not saved, so a copy from the save starts over without it: {json.Error}"
        );
    }

    /// <summary>
    /// Reads <paramref name="json"/>, the saved member <paramref name="name"/> of <paramref name="type"/>, as
    /// <paramref name="memberType"/>; false, logged one time, when it no longer fits.
    /// </summary>
    private bool TryRead(Type type, string name, JsonElement json, Type memberType, out object? value)
    {
        // A reference that the copy held none of: what it is set back to.
        if (json.ValueKind == JsonValueKind.Null)
        {
            value = null;

            return true;
        }

        Result<object> read = Json.Read(json, memberType);
        if (read.Ok)
        {
            value = read.Value;

            return true;
        }

        LogOnce(type, name, $"Script {type.Name} cannot read {json} as its {name}: {read.Error}");
        value = null;

        return false;
    }

    /// <summary>
    /// Logs <paramref name="message"/> about the member <paramref name="name"/> of <paramref name="type"/>, the first
    /// time only, as the hook that finds it runs each save or each start.
    /// </summary>
    private void LogOnce(Type type, string name, string message)
    {
        if (_logged.Add($"{type.FullName}.{name}"))
            Log.Error(message);
    }
}
