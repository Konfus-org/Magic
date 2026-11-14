using System.Reflection;
using System.Text.Json;
using Magic.Debugging;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Files;
using Magic.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Magic.CSharpScripting;

/// <summary>
/// The state of a copy of a script as JSON by name, both ways: out of a copy for a save, and into a copy that a save
/// made. It is the values that a prefab sets, by the name a file gives them, and the other fields that the copy keeps
/// for itself, by name.
/// </summary>
/// <remarks>
/// A field that is no data of the copy's own is left out, as a copy gets it again when it is made: its entity, a
/// thing that its constructor is handed, a service, a delegate, a task or a disposable. A member that JSON cannot
/// hold, or that the class no longer has, goes to the log one time. Game thread.
/// </remarks>
internal sealed class ScriptState(IServiceProvider services)
{
    private const BindingFlags AnyField = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    /// <summary>
    /// The members that did not go into a save or come back out of one, as <c>Class.member</c>, each logged one time.
    /// </summary>
    private readonly HashSet<string> _logged = new(StringComparer.Ordinal);

    /// <summary>
    /// Answers which types the provider gives, to leave a service that a copy took out of a save.
    /// </summary>
    private readonly IServiceProviderIsService? _given = services as IServiceProviderIsService;

    /// <summary>
    /// The members of each script class, searched the first time a copy of it is read or written.
    /// </summary>
    private readonly Dictionary<Type, Members> _members = [];

    /// <summary>
    /// The state of <paramref name="script"/> as it is now, for a save.
    /// </summary>
    public List<KeyValuePair<string, JsonElement>> Read(IScript script)
    {
        Type type = script.GetType();
        List<KeyValuePair<string, JsonElement>> state = [];
        foreach (SavedMember member in GetMembers(type).Saved)
        {
            Add(state, type, member.Name, member.Type, member.Get(script));
        }

        return state;
    }

    /// <summary>
    /// Sets each value of <paramref name="values"/>, which a prefab gives, on the public settable property of
    /// <paramref name="script"/> of that name, whatever the case of its first letter.
    /// </summary>
    public void SetValues(ScriptValues values, IScript script, Entity entity)
    {
        Members members = GetMembers(script.GetType());
        foreach ((string name, JsonElement value) in values.All)
        {
            Set(script, entity, name, value, members.Values.GetValueOrDefault(name));
        }
    }

    /// <summary>
    /// Sets each member of <paramref name="state"/>, which a save holds, on <paramref name="script"/>: the public
    /// settable property of that name, else the field of that name, which only a save sets.
    /// </summary>
    public void Restore(ScriptValues state, IScript script, Entity entity)
    {
        Members members = GetMembers(script.GetType());
        foreach ((string name, JsonElement value) in state.All)
        {
            // Else the field: its own of that name, or the one that the compiler made for a property or a primary
            // constructor parameter of that name.
            MemberInfo? member =
                (MemberInfo?)members.Values.GetValueOrDefault(name) ?? members.Fields.GetValueOrDefault(name);
            Set(script, entity, name, value, member);
        }
    }

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
    /// The members of <paramref name="type"/>: searched the first time, and the same ones for every copy after.
    /// </summary>
    private Members GetMembers(Type type)
    {
        if (_members.TryGetValue(type, out Members? found))
            return found;

        Members members = new();

        // The values first, each one's backing field noted, so the fields below do not write it a second time.
        HashSet<string> backing = new(StringComparer.Ordinal);
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (
                property.GetMethod is not { IsPublic: true }
                || property.SetMethod is not { IsPublic: true }
                || property.GetIndexParameters().Length != 0
            )
            {
                continue;
            }

            members.Values.TryAdd(property.Name, property);
            backing.Add($"<{property.Name}>k__BackingField");

            // A value is saved by the name that a prefab gives it.
            string key = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            members.Saved.Add(new SavedMember(key, property.PropertyType, property.GetValue));
        }

        // The types that a copy is handed when it is made: the parameters of the one public constructor of a script.
        ConstructorInfo[] constructors = type.GetConstructors();
        Type[] handed =
            constructors.Length == 1
                ? Array.ConvertAll(constructors[0].GetParameters(), parameter => parameter.ParameterType)
                : [];
        foreach (FieldInfo field in type.GetFields(AnyField))
        {
            // A field of the class's own wins over one that the compiler made for a member of the same name.
            string name = GetFieldName(field);
            if (name == field.Name || !members.Fields.ContainsKey(name))
                members.Fields[name] = field;

            if (!backing.Contains(field.Name) && !IsHanded(field.FieldType, handed))
                members.Saved.Add(new SavedMember(name, field.FieldType, field.GetValue));
        }

        _members.Add(type, members);

        return members;
    }

    /// <summary>
    /// Whether a field of <paramref name="type"/> is no data of the copy's own, so a save leaves it out: its entity, a
    /// delegate, a task, a disposable, one of the <paramref name="handed"/> types, or a service, each of which a copy
    /// gets again when it is made.
    /// </summary>
    /// <remarks>
    /// A sequence is data, not a service: the provider answers for every <see cref="IEnumerable{T}"/>, and a copy's
    /// own list is its data.
    /// </remarks>
    private bool IsHanded(Type type, Type[] handed) =>
        type == typeof(Entity)
        || typeof(Delegate).IsAssignableFrom(type)
        || typeof(Task).IsAssignableFrom(type)
        || typeof(IDisposable).IsAssignableFrom(type)
        || (!type.IsValueType && Array.Exists(handed, given => given == type))
        || (
            !(type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            && _given?.IsService(type) == true
        );

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
    /// Adds <paramref name="value"/>, the member <paramref name="name"/> of <paramref name="type"/>, to
    /// <paramref name="state"/> as JSON; one that JSON cannot hold goes to the log one time and is left out.
    /// </summary>
    private void Add(
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

    /// <summary>
    /// A member that a save keeps: the name it is saved as, the type it holds, and how to read it out of a copy.
    /// </summary>
    private readonly record struct SavedMember(string Name, Type Type, Func<IScript, object?> Get);

    /// <summary>
    /// The members of one script class that a prefab and a save reach.
    /// </summary>
    private sealed class Members
    {
        /// <summary>
        /// The values, which a prefab sets: the public properties that have both a public getter and setter, by name,
        /// whatever the case of its first letter.
        /// </summary>
        public Dictionary<string, PropertyInfo> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Every field by the name it is saved as, for the state that only a save sets.
        /// </summary>
        public Dictionary<string, FieldInfo> Fields { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// What a save keeps of a copy: the values, then the fields that are data of the copy's own.
        /// </summary>
        public List<SavedMember> Saved { get; } = [];
    }
}
