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
/// The settings of each gem in one place: the <see cref="ISettings"/> part of each gem that has one, changed in place.
/// </summary>
/// <remarks>
/// <para>
/// A key is the <see cref="CategoryAttribute"/> of the settings class or of the member, then the name of the member or
/// its <see cref="JsonPropertyNameAttribute"/>, as <c>Rendering.Resolution</c>. Use it on the game thread, or while no
/// frame runs.
/// </para>
/// <para>
/// A gem is first made with the defaults of its settings, then what <see cref="SaveAsync"/> saved for this user and
/// project, then what <c>--configure</c> gave at launch. When it is made again, after a restart or a rebuild of its
/// code, it runs with what it last ran with. Each holds only the members that differ from the defaults, as JSON. The
/// saved settings are in <c>Magic/&lt;project&gt;/Settings/&lt;class&gt;.json</c> in the local application data of
/// the user, never in the project.
/// </para>
/// </remarks>
public sealed class Settings
{
    private readonly App _app;

    private readonly IFileSystem _files;

    /// <summary>
    /// The settings registered, in the order their gems were made.
    /// </summary>
    private ImmutableArray<RegisteredSettings> _registered = [];

    /// <summary>
    /// What each settings class last ran with in this process, by its project and name, as
    /// <c>Forest/PlayerSettings</c>.
    /// </summary>
    /// <remarks>
    /// It is a JSON object of the members that differ from the defaults. It outlasts the types of the class and, when
    /// it is there, it replaces what was saved or given at launch.
    /// </remarks>
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
            foreach (RegisteredSettings registered in _registered)
            {
                foreach (SettingMember member in registered.Members)
                {
                    options.Add(registered.ToSetting(member));
                }
            }

            return options.ToImmutable();
        }
    }

    /// <summary>
    /// The settings that the gem with <typeparamref name="TSettings"/> runs with; with no such gem, what it would get.
    /// </summary>
    public TSettings Get<TSettings>()
        where TSettings : ISettings => (TSettings)Get(typeof(TSettings));

    /// <summary>
    /// Writes each member of <paramref name="settings"/> on the settings of the gem with
    /// <typeparamref name="TSettings"/>, and keeps them for when the gem is made again.
    /// </summary>
    /// <remarks>
    /// <paramref name="settings"/> can be that same object, after a change to it. A member with
    /// <see cref="RequireRestartAttribute"/> that differs from what the gem was made with restarts the run.
    /// </remarks>
    public void Set<TSettings>(TSettings settings)
        where TSettings : ISettings
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (Find(typeof(TSettings)) is { } registered)
            Set(registered, settings);
        else
            Keep(GetLiveKey(typeof(TSettings)), settings);
    }

    /// <summary>
    /// Sets the member that <paramref name="key"/> names, as <c>Rendering.Resolution</c>, to <paramref name="value"/>,
    /// or returns why not.
    /// </summary>
    /// <remarks>
    /// It sets the value in a copy of the settings of the gem, then sets the copy as
    /// <see cref="Set{TSettings}(TSettings)"/> does. It fails when no gem has the key, or the member does not take the
    /// value. Use it in tools that reach a setting by its key, such as the console. Game code that knows the settings
    /// class uses <see cref="Get{TSettings}"/> and <see cref="Set{TSettings}(TSettings)"/>.
    /// </remarks>
    public Result Set<T>(string key, T value)
    {
        foreach (RegisteredSettings registered in _registered)
        {
            if (registered.Members.FirstOrDefault(member => member.Key == key) is not { } member)
                continue;

            Result<JsonElement> written = Json.Write(value, value?.GetType() ?? typeof(T));
            if (!written.Ok)
                return Result.Failure(written.Error);

            object settings = registered.Value;
            JsonObject json = ToJson(settings);
            json[member.JsonName] = JsonNode.Parse(written.Value.GetRawText());
            Result<object> changed = Json.Read(
                JsonSerializer.SerializeToElement(json, Json.Options),
                settings.GetType()
            );
            if (!changed.Ok)
                return Result.Failure($"{key} takes a {member.Type.Name}: {changed.Error}");

            Set(registered, changed.Value);

            return Result.Success();
        }

        return Result.Failure($"no gem has the setting {key}; get lists them.");
    }

    /// <summary>
    /// Writes the members of each registered gem that differ from their defaults to the settings file of this user in
    /// the open project.
    /// </summary>
    /// <remarks>
    /// The gem is made with them from then on. A settings file with no such members is removed. It returns why not when
    /// it fails.
    /// </remarks>
    public async Task<Result> SaveAsync(CancellationToken cancel)
    {
        if (_app.Project is not { } project)
            return Result.Failure("no project is open, and settings are saved per project.");

        // What each gem runs by now, which it may have set itself, taken before the writes leave the game thread.
        List<(string File, JsonElement Changed)> saves = [];
        foreach (RegisteredSettings registered in _registered)
        {
            object settings = registered.Value;
            Keep(registered.LiveKey, settings);
            saves.Add((GetFile(project, settings.GetType()), _live[registered.LiveKey]));
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
    /// Sets each of <paramref name="pairs"/>, written <c>Key=value</c>, and logs each that is not set: the settings
    /// given with <c>--configure</c>.
    /// </summary>
    /// <remarks>
    /// <see cref="ValueText"/> reads the value. Before a project opens, no gem is made, so each pair waits for its gem,
    /// over what was saved.
    /// </remarks>
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

            Result<object> parsed = _registered
                .SelectMany(registered => registered.Members)
                .FirstOrDefault(member => member.Key == key)
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
    /// Lists <paramref name="settings"/>, a part of a gem, until the gem ends and disposes the subscription.
    /// </summary>
    /// <remarks>
    /// Then it keeps what the gem last ran with for when the gem is made again. It throws, with the name of the gem,
    /// for settings without a category or with a key that another gem has.
    /// </remarks>
    internal Subscription Register(ISettings settings)
    {
        Type type = settings.GetType();
        ImmutableArray<SettingMember> members = GetMembers(type);
        HashSet<string> keys = [.. members.Select(member => member.Key)];
        foreach (RegisteredSettings other in _registered)
        {
            string? clash = other.Members.Select(member => member.Key).FirstOrDefault(keys.Contains);
            if (clash is not null)
            {
                throw new InvalidOperationException(
                    $"{GemRegistry.GetGemName(other.Value.GetType())} and {GemRegistry.GetGemName(type)} both have the setting {clash}."
                );
            }
        }

        // A copy of what the gem is made with, which a later change is compared with.
        object madeWith = CreateDefaults(type);
        foreach (SettingMember member in members)
            member.Set(madeWith, member.Get(settings));

        // The key is taken now, as the project may change before the gem ends.
        RegisteredSettings registered = new(settings, madeWith, members, ReadSummaries(type), GetLiveKey(type));
        _registered = _registered.Add(registered);

        return new Subscription(() =>
        {
            Keep(registered.LiveKey, registered.Value);
            _registered = _registered.Remove(registered);
        });
    }

    private static object CreateDefaults(Type type) =>
        Activator.CreateInstance(type) ?? throw new InvalidOperationException($"{type.Name} could not be made.");

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
                $"{GemRegistry.GetGemName(type)}'s {type.Name} has no [Category], which its settings' keys start with."
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

    private RegisteredSettings? Find(Type type) =>
        _registered.FirstOrDefault(registered => registered.Value.GetType() == type);

    private string GetLiveKey(Type type) => $"{_app.Project?.Name}/{type.Name}";

    private string GetFile(Project project, Type type) =>
        _files.Combine(UserFolders.GetProjectFolder(_files, project), "Settings", $"{type.Name}.json");

    /// <summary>
    /// Makes <paramref name="type"/> as it last ran in this process, else as it was saved, with what was given at
    /// launch over either.
    /// </summary>
    /// <remarks>
    /// A member that it no longer has is left out, and a value that cannot be read stays at its default. Each gets a
    /// warning.
    /// </remarks>
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
    /// What this user saved for <paramref name="type"/> in the open project.
    /// </summary>
    /// <remarks>
    /// It is empty when no project is open, nothing was saved, or the file cannot be read, with a warning.
    /// </remarks>
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

    private void Set(RegisteredSettings registered, object settings)
    {
        SettingMember? restart = registered.Members.FirstOrDefault(member =>
            member.RequiresRestart && !Equals(member.Get(registered.MadeWith), member.Get(settings))
        );
        foreach (SettingMember member in registered.Members)
            member.Set(registered.Value, member.Get(settings));

        Keep(registered.LiveKey, settings);
        if (restart is null)
            return;

        Log.Info($"{restart.Key} changed, so the run restarts.");
        _app.Restart();
    }

    /// <summary>
    /// Keeps the members of <paramref name="settings"/> that differ from its defaults as what its class last ran with,
    /// under <paramref name="key"/>.
    /// </summary>
    /// <remarks>
    /// When the settings are back at their defaults, it keeps an empty object.
    /// </remarks>
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
        Result<string> docs = _files.ReadText(
            Path.ChangeExtension(GemRegistry.GetPath(_files, GemRegistry.GetGemName(type)), ".xml")
        );
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
    /// The settings of a gem: <paramref name="Value"/>, which its parts hold, and <paramref name="MadeWith"/>, a copy
    /// of what the gem was made with.
    /// </summary>
    /// <remarks>
    /// A change to a member with <see cref="RequireRestartAttribute"/> is compared with <paramref name="MadeWith"/>.
    /// </remarks>
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
