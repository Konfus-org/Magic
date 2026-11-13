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
/// thing that its constructor is handed, a delegate, a task or a disposable. A member that JSON cannot hold, or that
/// the class no longer has, goes to the log one time. Game thread.
/// </remarks>
internal sealed class ScriptState
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

        Type[] handed = GetHandedTypes(type);
        foreach (FieldInfo field in type.GetFields(AnyField))
        {
            if (!backing.Contains(field.Name) && !IsHanded(field.FieldType, handed))
                Read(state, type, GetFieldName(field), field.FieldType, field.GetValue(script));
        }

        return state;
    }

    /// <summary>
    /// Sets each value of <paramref name="values"/>, which a prefab gives, on the public settable property of
    /// <paramref name="script"/> of that name, whatever the case of its first letter.
    /// </summary>
    public void SetValues(ScriptValues values, IScript script, Entity entity)
    {
        Type type = script.GetType();
        foreach ((string name, JsonElement value) in values.All)
        {
            Set(script, entity, name, value, FindValue(type, name));
        }
    }

    /// <summary>
    /// Sets each member of <paramref name="state"/>, which a save holds, on <paramref name="script"/>: the public
    /// settable property of that name, else the field of that name, which only a save sets.
    /// </summary>
    public void Restore(ScriptValues state, IScript script, Entity entity)
    {
        Type type = script.GetType();
        foreach ((string name, JsonElement value) in state.All)
        {
            Set(script, entity, name, value, (MemberInfo?)FindValue(type, name) ?? FindField(type, name));
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
    /// The types that a copy of <paramref name="type"/> is handed when it is made: the parameters of the one public
    /// constructor that a script has.
    /// </summary>
    private static Type[] GetHandedTypes(Type type)
    {
        ConstructorInfo[] constructors = type.GetConstructors();

        return constructors.Length == 1
            ? Array.ConvertAll(constructors[0].GetParameters(), parameter => parameter.ParameterType)
            : [];
    }

    /// <summary>
    /// Whether a field of <paramref name="type"/> is no data of the copy's own, so a save leaves it out: its entity, a
    /// delegate, a task, a disposable, or one of the <paramref name="handed"/> types, which a copy gets again when it
    /// is made.
    /// </summary>
    private static bool IsHanded(Type type, Type[] handed) =>
        type == typeof(Entity)
        || typeof(Delegate).IsAssignableFrom(type)
        || typeof(Task).IsAssignableFrom(type)
        || typeof(IDisposable).IsAssignableFrom(type)
        || (!type.IsValueType && Array.Exists(handed, given => type.IsAssignableFrom(given)));

    /// <summary>
    /// Sets <paramref name="value"/>, which a file names <paramref name="name"/>, on <paramref name="member"/> of
    /// <paramref name="script"/>.
    /// </summary>
    /// <remarks>
    /// A value that the script has no member of that name for, or that the member's type cannot hold, goes to the log
    /// and is left out, so the rest of the script still runs.
    /// </remarks>
    private void Set(IScript script, Entity entity, string name, JsonElement value, MemberInfo? member)
    {
        Type type = script.GetType();
        switch (member)
        {
            case PropertyInfo property:
                if (TryRead(type, name, value, property.PropertyType, out object? held))
                    property.SetValue(script, held);
                break;
            case FieldInfo field:
                if (TryRead(type, name, value, field.FieldType, out object? kept))
                    field.SetValue(script, kept);
                break;
            default:
                LogOnce(type, name, $"Script {type.Name} on {entity} has no settable {name}, so {value} is left out.");
                break;
        }
    }

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
