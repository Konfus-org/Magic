using System.Collections.Immutable;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Xml.Linq;
using Magic.Debugging;
using Magic.Files;
using Magic.Projects;
using Magic.Utils;

namespace Magic.Configuration;

/// <summary>
/// Every gem's settings in one place: the <see cref="ISettings"/> part of each gem that has one, which it changes in
/// place. A key is the settings class's or the member's <see cref="CategoryAttribute"/>, then the member's name, or its
/// <see cref="JsonPropertyNameAttribute"/>, as <c>Rendering.Resolution</c>. Game thread, or while no frame runs.
/// <para>
/// A gem is first made with its settings' defaults, then what <see cref="SaveAsync"/> saved for this user and project,
/// then what <c>--configure</c> gave at launch; made again, after a restart or a rebuild of its code, it runs by what
/// it last ran by. Each holds only the members that differ from the defaults, as JSON; what is saved is in
/// <c>Magic/&lt;project&gt;/Settings/&lt;class&gt;.json</c> under the user's local application data, never in the
/// project.
/// </para>
/// </summary>
public sealed class Settings
{
    private readonly App _app;

    private readonly IFileSystem _files;

    /// <summary>
    /// The settings registered, in the order their gems were made.
    /// </summary>
    private ImmutableArray<RegisteredSettings> _gems = [];

    /// <summary>
    /// What each settings class last ran by in this process, by its project and name, as <c>Forest/PlayerSettings</c>:
    /// a JSON object of the members that differ from its defaults, which outlasts the class's types and, once there,
    /// stands in for what was saved or given at launch.
    /// </summary>
    private ImmutableDictionary<string, JsonElement> _live = [];

    /// <summary>
    /// Each member given at launch, by key, as text until its settings are made and says what it is.
    /// </summary>
    private ImmutableDictionary<string, string> _given = [];

    internal Settings(App app, IFileSystem files)
    {
        _app = app;
        _files = files;
    }

    /// <summary>
    /// The options: every member of every registered gem's settings, in the order the gems were made.
    /// </summary>
    public ImmutableArray<Setting> Options
    {
        get
        {
            ImmutableArray<Setting>.Builder options = ImmutableArray.CreateBuilder<Setting>();
            foreach (RegisteredSettings gem in _gems)
            {
                foreach (SettingMember member in gem.Members)
                {
                    options.Add(gem.ToSetting(member));
                }
            }

            return options.ToImmutable();
        }
    }

    /// <summary>
    /// The settings the gem with <typeparamref name="TSettings"/> runs by; with no such gem, what it would be made with.
    /// </summary>
    public TSettings Get<TSettings>()
        where TSettings : ISettings => (TSettings)Get(typeof(TSettings));

    /// <summary>
    /// Writes each member of <paramref name="settings"/> on the settings the gem with <typeparamref name="TSettings"/>
    /// runs by, and keeps them for when it is made again. <paramref name="settings"/> may be that same object, after a
    /// change to it. A member with <see cref="RequireRestartAttribute"/> that differs from what the gem was made with
    /// restarts the run.
    /// </summary>
    public void Set<TSettings>(TSettings settings)
        where TSettings : ISettings
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (Find(typeof(TSettings)) is { } gem)
            Set(gem, settings);
        else
            Keep(GetLiveKey(typeof(TSettings)), settings);
    }

    /// <summary>
    /// Sets the member <paramref name="key"/> names, as <c>Rendering.Resolution</c>, to <paramref name="value"/> in a
    /// copy of its gem's settings, which it then sets as <see cref="Set{TSettings}(TSettings)"/> does; or says why not:
    /// no gem has the key, or the member doesn't take the value. For tools that reach a setting by its key, such as the
    /// console; game code that knows the settings class uses <see cref="Get{TSettings}"/> and
    /// <see cref="Set{TSettings}(TSettings)"/>.
    /// </summary>
    public Result Set<T>(string key, T value)
    {
        foreach (RegisteredSettings gem in _gems)
        {
            if (gem.Members.FirstOrDefault(member => member.Key == key) is not { } member)
                continue;

            Result<JsonElement> written = Json.Write(value, value?.GetType() ?? typeof(T));
            if (!written.Ok)
                return Result.Failure(written.Error);

            object settings = gem.Value;
            JsonObject json = ToJson(settings);
            json[member.JsonName] = JsonNode.Parse(written.Value.GetRawText());
            Result<object> changed = Json.Read(
                JsonSerializer.SerializeToElement(json, Json.Options),
                settings.GetType()
            );
            if (!changed.Ok)
                return Result.Failure($"{key} takes a {member.Type.Name}: {changed.Error}");

            Set(gem, changed.Value);

            return Result.Success();
        }

        return Result.Failure($"no gem has the setting {key}; get lists them.");
    }

    /// <summary>
    /// Writes each registered gem's members that differ from their defaults to this user's file for its settings in the
    /// open project, which the gem is made with from then on; settings with none have their file removed. Or says why
    /// not.
    /// </summary>
    public async Task<Result> SaveAsync(CancellationToken cancel)
    {
        if (_app.Project is not { } project)
            return Result.Failure("no project is open, and settings are saved per project.");

        // What each gem runs by now, which it may have set itself, taken before the writes leave the game thread.
        List<(string File, JsonElement Changed)> saves = [];
        foreach (RegisteredSettings gem in _gems)
        {
            object settings = gem.Value;
            Keep(gem.LiveKey, settings);
            saves.Add((GetFile(project, settings.GetType()), _live[gem.LiveKey]));
        }

        foreach ((string file, JsonElement changed) in saves)
        {
            Result saved;
            if (changed.EnumerateObject().Any())
            {
                byte[] json = JsonSerializer.SerializeToUtf8Bytes(changed, Json.Indented);
                saved = await _files.WriteAsync(file, json, cancel).ConfigureAwait(false);
            }
            else
            {
                saved = _files.FileExists(file) ? _files.Delete(file) : Result.Success();
            }

            if (!saved.Ok)
                return saved;
        }

        return Result.Success();
    }

    /// <summary>
    /// <see cref="Get{TSettings}"/> of a type known only at run time, as the services hand it to a gem's constructor.
    /// </summary>
    internal object Get(Type type) => Find(type)?.Value ?? CreateSettings(type);

    /// <summary>
    /// Sets each of <paramref name="pairs"/>, written <c>Key=value</c> with the value as <see cref="ValueText"/> reads
    /// it, and logs each that isn't set: the settings given with <c>--configure</c>. Before a project opens no gem is
    /// made, so each waits for its gem, over what was saved.
    /// </summary>
    internal void Configure(string[] pairs)
    {
        foreach (string pair in pairs)
        {
            string[] keyAndValue = pair.Split('=', 2, StringSplitOptions.TrimEntries);
            if (keyAndValue.Length != 2)
            {
                Log.Error($"--configure {pair} was not set: it is no Key=value.");
                continue;
            }

            (string key, string text) = (keyAndValue[0], keyAndValue[1]);
            if (_app.Project is null)
            {
                _given = _given.SetItem(key, text);
                continue;
            }

            Result<object> parsed = _gems.SelectMany(gem => gem.Members).FirstOrDefault(member => member.Key == key)
                is { } member
                ? ValueText.Parse(member.Type, ValueText.Split(text))
                : Result.Failure($"no gem has the setting {key}; get lists them.");
            Result set = parsed.Ok ? Set(key, parsed.Value) : Result.Failure(parsed.Error);
            if (!set.Ok)
                Log.Error($"--configure {pair} was not set: {set.Error}");
        }
    }

    /// <summary>
    /// Logs, once the gems are made, each member given at launch that none of them has, and forgets them.
    /// </summary>
    internal void ReportUnknownKeys()
    {
        foreach (string key in _given.Keys.Order(StringComparer.Ordinal))
            Log.Error($"--configure {key} was not set: no gem has the setting {key}; get lists them.");

        _given = [];
    }

    /// <summary>
    /// Lists <paramref name="part"/>, a gem's part, until the subscription is disposed, as the gem ends; then what it
    /// last ran by is kept for when it is made again. A part that is not an <see cref="ISettings"/> changes nothing.
    /// Throws, naming the gem, for settings without a category or a key another gem has.
    /// </summary>
    internal Subscription Register(object part)
    {
        if (part is not ISettings settings)
            return new Subscription(static () => { });

        Type type = settings.GetType();
        ImmutableArray<SettingMember> members = GetMembers(type);
        HashSet<string> keys = [.. members.Select(member => member.Key)];
        foreach (RegisteredSettings other in _gems)
        {
            string? clash = other.Members.Select(member => member.Key).FirstOrDefault(keys.Contains);
            if (clash is not null)
            {
                throw new InvalidOperationException(
                    $"{GetName(other.Value.GetType())} and {GetName(type)} both have the setting {clash}."
                );
            }
        }

        // The key is taken now, as the project may change before the gem ends.
        RegisteredSettings registered = new(
            settings,
            Copy(settings, members),
            members,
            ReadSummaries(type),
            GetLiveKey(type)
        );
        _gems = _gems.Add(registered);

        return new Subscription(() =>
        {
            Keep(registered.LiveKey, registered.Value);
            _gems = _gems.Remove(registered);
        });
    }

    private static string GetName(Type type) => type.Assembly.GetName().Name ?? type.Name;

    private static object CreateDefaults(Type type) =>
        Activator.CreateInstance(type) ?? throw new InvalidOperationException($"{type.Name} could not be made.");

    /// <summary>
    /// A copy of <paramref name="settings"/>: a new object of its class, with each of <paramref name="members"/> set
    /// to the same value.
    /// </summary>
    private static object Copy(object settings, ImmutableArray<SettingMember> members)
    {
        object copy = CreateDefaults(settings.GetType());
        foreach (SettingMember member in members)
            member.Set(copy, member.Get(settings));

        return copy;
    }

    private static JsonObject ToJson(object settings) =>
        JsonSerializer.SerializeToNode(settings, settings.GetType(), Json.Options) as JsonObject
        ?? throw new InvalidOperationException($"{settings.GetType().Name} is no JSON object.");

    /// <summary>
    /// <paramref name="value"/> as <see cref="ValueText"/> reads it for <paramref name="member"/>, as JSON.
    /// </summary>
    private static Result<JsonNode?> Parse(SettingMember member, string value)
    {
        Result<object> parsed = ValueText.Parse(member.Type, ValueText.Split(value));

        return parsed.Ok
            ? Result.Success(JsonSerializer.SerializeToNode(parsed.Value, member.Type, Json.Options))
            : Result.Failure(parsed.Error);
    }

    /// <summary>
    /// The members of <paramref name="type"/> that settings list, as JSON reads them.
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
            if (json is not { AttributeProvider: PropertyInfo property, Get: { } get, Set: not null })
                continue;

            string group = property.GetCustomAttribute<CategoryAttribute>()?.Category ?? category.Category;
            string name = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name;
            bool requiresRestart = property.IsDefined(typeof(RequireRestartAttribute));
            members.Add(
                new SettingMember(
                    $"{group}.{name}",
                    group,
                    property.Name,
                    json.Name,
                    json.PropertyType,
                    get,
                    json.Set,
                    requiresRestart
                )
            );
        }

        return [.. members];
    }

    private RegisteredSettings? Find(Type type) => _gems.FirstOrDefault(gem => gem.Value.GetType() == type);

    private string GetLiveKey(Type type) => $"{_app.Project?.Name}/{type.Name}";

    private string GetFile(Project project, Type type) =>
        _files.Combine(UserFolders.GetProjectFolder(_files, project), "Settings", $"{type.Name}.json");

    /// <summary>
    /// <paramref name="type"/> as it last ran in this process, or else as it was saved, with what was given at launch
    /// over either; a member it no longer has is left out, and what can't be read leaves it at its defaults, each with a
    /// warning.
    /// </summary>
    private object CreateSettings(Type type)
    {
        ImmutableArray<SettingMember> members = GetMembers(type);
        JsonObject values = _live.TryGetValue(GetLiveKey(type), out JsonElement live)
            ? JsonObject.Create(live) ?? []
            : ReadSaved(type);
        foreach (SettingMember member in members)
        {
            if (!_given.TryGetValue(member.Key, out string? text))
                continue;

            _given = _given.Remove(member.Key);
            Result<JsonNode?> parsed = Parse(member, text);
            if (parsed.Ok)
                values[member.JsonName] = parsed.Value;
            else
                Log.Error($"--configure {member.Key}={text} was not set: {parsed.Error}");
        }

        string[] unknown =
        [
            .. values.Select(pair => pair.Key).Where(name => members.All(member => member.JsonName != name)),
        ];
        foreach (string name in unknown)
            values.Remove(name);

        if (unknown.Length > 0)
            Log.Warn(
                $"{type.Name} has no {string.Join(", ", unknown)} now, so what was saved or set for them is left out."
            );

        Result<object> read = Json.Read(JsonSerializer.SerializeToElement(values, Json.Options), type);
        if (read.Ok)
            return read.Value;

        Log.Warn($"{type.Name} starts at its defaults: what was saved or set is not one now: {read.Error}");

        return CreateDefaults(type);
    }

    /// <summary>
    /// What this user saved for <paramref name="type"/> in the open project; nothing when no project is open, nothing
    /// was saved, or, with a warning, the file can't be read.
    /// </summary>
    private JsonObject ReadSaved(Type type)
    {
        if (_app.Project is not { } project)
            return [];

        string file = GetFile(project, type);
        if (!_files.FileExists(file))
            return [];

        Result<byte[]> bytes = _files.ReadBytes(file);
        Result<JsonElement> read = bytes.Ok ? Json.Read<JsonElement>(bytes.Value) : Result.Failure(bytes.Error);
        if (read.Ok && read.Value.ValueKind == JsonValueKind.Object)
            return JsonObject.Create(read.Value) ?? [];

        Log.Warn(
            $"{file} was not read, so {type.Name} is made without it: {(read.Ok ? "it is no JSON object." : read.Error)}"
        );

        return [];
    }

    private void Set(RegisteredSettings gem, object settings)
    {
        SettingMember? restart = gem.Members.FirstOrDefault(member =>
            member.RequiresRestart && !Equals(member.Get(gem.MadeWith), member.Get(settings))
        );
        foreach (SettingMember member in gem.Members)
            member.Set(gem.Value, member.Get(settings));

        Keep(gem.LiveKey, settings);
        if (restart is null)
            return;

        Log.Info($"{restart.Key} changed, so the run restarts.");
        _app.Restart();
    }

    /// <summary>
    /// Keeps the members of <paramref name="settings"/> that differ from its defaults as what its class last ran by,
    /// under <paramref name="key"/>: none, when it is back at its defaults, is kept too.
    /// </summary>
    private void Keep(string key, object settings)
    {
        JsonObject changed = ToJson(settings);
        foreach ((string name, JsonNode? value) in ToJson(CreateDefaults(settings.GetType())))
        {
            if (JsonNode.DeepEquals(changed[name], value))
                changed.Remove(name);
        }

        _live = _live.SetItem(key, JsonSerializer.SerializeToElement(changed, Json.Options));
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
        string Category,
        string Name,
        string JsonName,
        Type Type,
        Func<object, object?> Get,
        Action<object, object?> Set,
        bool RequiresRestart
    );

    /// <summary>
    /// A gem's settings: <paramref name="Value"/>, which its parts hold, and <paramref name="MadeWith"/>, a copy of
    /// what the gem was made with, which a change to a member with <see cref="RequireRestartAttribute"/> compares with.
    /// </summary>
    private sealed record RegisteredSettings(
        ISettings Value,
        object MadeWith,
        ImmutableArray<SettingMember> Members,
        ImmutableDictionary<string, string> Summaries,
        string LiveKey
    )
    {
        public Setting ToSetting(SettingMember member) =>
            new(
                member.Key,
                member.Category,
                member.Get(Value),
                member.Type,
                Summaries.GetValueOrDefault(member.Name, string.Empty),
                member.RequiresRestart
            );
    }
}
