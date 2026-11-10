using System.Collections.Immutable;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Xml.Linq;
using Magic.Debugging;
using Magic.Files;
using Magic.Projects;
using Magic.Utils;

namespace Magic.Configuration;

/// <summary>
/// Every gem's settings in one place. It holds the gems, not the values: each gem with settings
/// (<see cref="IHasSettings{TSettings}"/>) keeps its own record. A key is the record's or the member's
/// <see cref="CategoryAttribute"/>, then the member's name, or its <see cref="JsonPropertyNameAttribute"/>, as
/// <c>Rendering.Resolution</c>. It lasts the whole process, and keeps what differs from each record's defaults when its
/// gem ends, so the gem made again, after a restart or a rebuild of its code, starts with what was set. Game thread, or
/// while no frame runs.
/// </summary>
public sealed class Settings
{
    /// <summary>
    /// What a <c>with</c> does to a sealed record with nothing to set: a copy of every field.
    /// </summary>
    private static readonly MethodInfo _copy =
        typeof(object).GetMethod(nameof(MemberwiseClone), BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("object has no MemberwiseClone.");

    private readonly App _app;

    private readonly IFileSystem _files;

    /// <summary>
    /// The gems registered, in the order they were made.
    /// </summary>
    private ImmutableArray<RegisteredGem> _gems = [];

    /// <summary>
    /// Each member set to something other than its default, by key, as JSON, which outlasts its gem's types.
    /// </summary>
    private ImmutableDictionary<string, JsonElement> _changed = [];

    internal Settings(App app, IFileSystem files)
    {
        _app = app;
        _files = files;
    }

    /// <summary>
    /// Every member of every registered gem's settings, in the order the gems were made.
    /// </summary>
    public ImmutableArray<Setting> All =>
        [.. _gems.SelectMany(gem => gem.Members.Select(member => gem.ToSetting(member)))];

    /// <summary>
    /// The settings the gem with <typeparamref name="TSettings"/> runs by; with no such gem, its defaults with what was
    /// set before.
    /// </summary>
    public TSettings Get<TSettings>()
        where TSettings : class => (TSettings)Get(typeof(TSettings));

    /// <summary>
    /// Gives the gem with <typeparamref name="TSettings"/> <paramref name="settings"/>, and keeps them for when it is
    /// made again. A changed member with <see cref="RequireRestartAttribute"/> restarts the run.
    /// </summary>
    public void Set<TSettings>(TSettings settings)
        where TSettings : class
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (Find(typeof(TSettings)) is { } gem)
            Set(gem, settings);
        else
            Keep(GetMembers(typeof(TSettings)), settings);
    }

    /// <summary>
    /// Sets the member <paramref name="key"/> names to <paramref name="value"/>, as <see cref="ValueText"/> reads it,
    /// in a copy of its gem's settings, which it then sets; or says why not. String keys are for tools such as the
    /// console; game code uses <see cref="Get{TSettings}"/> and <see cref="Set{TSettings}(TSettings)"/>.
    /// </summary>
    public Result Set(string key, string value)
    {
        foreach (RegisteredGem gem in _gems)
        {
            if (gem.Members.FirstOrDefault(member => member.Key == key) is not { } member)
                continue;

            Result<object> parsed = ValueText.Parse(member.Type, ValueText.Split(value));
            if (!parsed.Ok)
                return Result.Failure(parsed.Error);

            object changed = _copy.Invoke(gem.Read(), null) ?? throw new InvalidOperationException("A copy is null.");
            member.Set(changed, parsed.Value);
            Set(gem, changed);

            return Result.Success();
        }

        return Result.Failure($"no gem has the setting {key}; get lists them.");
    }

    /// <summary>
    /// The <see cref="IHasSettings{TSettings}"/> <paramref name="gem"/> implements, or null when it has no settings.
    /// </summary>
    internal static Type? GetSettingsContract(Type gem) =>
        gem.GetInterfaces()
            .FirstOrDefault(contract =>
                contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IHasSettings<>)
            );

    /// <summary>
    /// <see cref="Get{TSettings}"/> of a type known only at run time, as the services hand it to a gem's constructor.
    /// </summary>
    internal object Get(Type type) => Find(type)?.Read() ?? CreateSettings(type);

    /// <summary>
    /// Lists <paramref name="gem"/>'s settings until the subscription is disposed, as the gem ends; then what it last
    /// ran by is kept for when it is made again. A gem without settings changes nothing. Throws, naming the gem, for a
    /// record without a category or a key another gem has.
    /// </summary>
    internal Subscription Register(object gem)
    {
        if (GetSettingsContract(gem.GetType()) is not { } contract)
            return new Subscription(static () => { });

        Type type = contract.GetGenericArguments()[0];
        ImmutableArray<SettingMember> members = GetMembers(type);
        HashSet<string> keys = [.. members.Select(member => member.Key)];
        foreach (RegisteredGem other in _gems)
        {
            string? clash = other.Members.Select(member => member.Key).FirstOrDefault(keys.Contains);
            if (clash is not null)
            {
                throw new InvalidOperationException(
                    $"{GetName(other.Gem.GetType())} and {GetName(gem.GetType())} both have the setting {clash}."
                );
            }
        }

        PropertyInfo property =
            contract.GetProperty(nameof(IHasSettings<>.Settings))
            ?? throw new InvalidOperationException($"{contract.Name} has no Settings.");
        RegisteredGem registered = new(gem, property, members, ReadSummaries(type));
        _gems = _gems.Add(registered);

        return new Subscription(() =>
        {
            Keep(members, registered.Read());
            _gems = _gems.Remove(registered);
        });
    }

    private static string GetName(Type type) => type.Assembly.GetName().Name ?? type.Name;

    private static object CreateDefaults(Type type) =>
        Activator.CreateInstance(type) ?? throw new InvalidOperationException($"{type.Name} could not be made.");

    /// <summary>
    /// The members of <paramref name="type"/>'s records that settings list, as JSON reads them.
    /// </summary>
    private static ImmutableArray<SettingMember> GetMembers(Type type)
    {
        if (type.GetCustomAttribute<CategoryAttribute>() is not { } category)
        {
            throw new InvalidOperationException(
                $"{GetName(type)}'s {type.Name} has no [Category], which its settings' keys start with."
            );
        }

        List<SettingMember> members = [];
        foreach (JsonPropertyInfo json in Json.Options.GetTypeInfo(type).Properties)
        {
            if (json is not { AttributeProvider: PropertyInfo property, Get: { } get, Set: { } set })
                continue;

            string group = property.GetCustomAttribute<CategoryAttribute>()?.Category ?? category.Category;
            string name = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name;
            bool requiresRestart = property.IsDefined(typeof(RequireRestartAttribute));
            members.Add(
                new SettingMember($"{group}.{name}", property.Name, json.PropertyType, get, set, requiresRestart)
            );
        }

        return [.. members];
    }

    private RegisteredGem? Find(Type type) => _gems.FirstOrDefault(gem => gem.Property.PropertyType == type);

    /// <summary>
    /// <paramref name="type"/>'s defaults, with each member set before in its place; a member whose type changed since
    /// keeps its default.
    /// </summary>
    private object CreateSettings(Type type)
    {
        object settings = CreateDefaults(type);
        foreach (SettingMember member in GetMembers(type))
        {
            if (!_changed.TryGetValue(member.Key, out JsonElement json))
                continue;

            Result<object> read = Json.Read(json, member.Type);
            if (!read.Ok)
            {
                Log.Warn($"{member.Key} keeps its default: what was set is no {member.Type.Name} now: {read.Error}");
                continue;
            }

            member.Set(settings, read.Value);
        }

        return settings;
    }

    private void Set(RegisteredGem gem, object settings)
    {
        object old = gem.Read();
        gem.Property.SetValue(gem.Gem, settings);
        Keep(gem.Members, settings);
        SettingMember? restart = gem.Members.FirstOrDefault(member =>
            member.RequiresRestart && !Equals(member.Get(old), member.Get(settings))
        );
        if (restart is null)
            return;

        Log.Info($"{restart.Key} changed, so the run restarts.");
        _app.Restart();
    }

    /// <summary>
    /// Keeps each of <paramref name="members"/> that differs from its default in <paramref name="settings"/>, and
    /// forgets each that doesn't.
    /// </summary>
    private void Keep(ImmutableArray<SettingMember> members, object settings)
    {
        object defaults = CreateDefaults(settings.GetType());
        ImmutableDictionary<string, JsonElement>.Builder changed = _changed.ToBuilder();
        foreach (SettingMember member in members)
        {
            object? value = member.Get(settings);
            if (Equals(value, member.Get(defaults)))
            {
                changed.Remove(member.Key);
                continue;
            }

            Result<JsonElement> json = Json.Write(value, member.Type);
            if (json.Ok)
                changed[member.Key] = json.Value;
            else
                Log.Warn($"{member.Key} is not kept for the next run: {json.Error}");
        }

        _changed = changed.ToImmutable();
    }

    /// <summary>
    /// The summary of each of <paramref name="type"/>'s properties, by name, from its gem's doc file beside its
    /// assembly; none when the file is missing.
    /// </summary>
    private ImmutableDictionary<string, string> ReadSummaries(Type type)
    {
        Result<string> docs = _files.ReadText(Path.ChangeExtension(GemRegistry.GetPath(_files, GetName(type)), ".xml"));
        if (!docs.Ok)
            return [];

        // A property's doc is the member P:Namespace.Type.Property, its summary's text on one line.
        string prefix = $"P:{type.FullName?.Replace('+', '.')}.";
        ImmutableDictionary<string, string>.Builder summaries = ImmutableDictionary.CreateBuilder<string, string>();
        foreach (XElement member in XDocument.Parse(docs.Value).Descendants("member"))
        {
            if ((string?)member.Attribute("name") is not { } id || !id.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            if (member.Element("summary") is not { } summary)
                continue;

            string[] words = summary.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            summaries[id[prefix.Length..]] = string.Join(' ', words);
        }

        return summaries.ToImmutable();
    }

    private sealed record SettingMember(
        string Key,
        string Name,
        Type Type,
        Func<object, object?> Get,
        Action<object, object?> Set,
        bool RequiresRestart
    );

    private sealed record RegisteredGem(
        object Gem,
        PropertyInfo Property,
        ImmutableArray<SettingMember> Members,
        ImmutableDictionary<string, string> Summaries
    )
    {
        public object Read() =>
            Property.GetValue(Gem) ?? throw new InvalidOperationException($"{Gem.GetType().Name}.Settings is null.");

        public Setting ToSetting(SettingMember member) =>
            new(
                member.Key,
                member.Get(Read()) is { } value ? ValueText.Format(value) : "null",
                member.Type,
                Summaries.GetValueOrDefault(member.Name, string.Empty),
                member.RequiresRestart
            );
    }
}
