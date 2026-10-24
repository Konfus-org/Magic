using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Magic.Attributes;
using Magic.Contexts.Assets;
using Magic.Contexts.CmdLine;
using Magic.Contexts.Settings;
using Magic.Contexts.Subscriptions;
using Magic.Contexts.Utils;
using Magic.Interfaces;
using Magic.Internal;

namespace Magic.Services;

/// <summary>
/// The gems' settings, and changing them while running: what a game's options use, as a settings window does. A gem
/// with settings declares a class for them (<see cref="ISettings"/>), and the object its constructor gets is made here,
/// in layers, each winning over the one before: the class's initialisers, the default
/// <see cref="Contexts.Assets.Preset"/> (<c>Presets/Default.preset</c>, the project's own settings), the preset applied
/// over it, <c>--set</c>, then what is <see cref="TrySet{T}"/> while running, until another preset is applied. A
/// setting is named by its key, <c>Gem.path</c>: the gem, as the project lists it, then where the setting sits in its
/// settings, with dots between levels. <see cref="All"/> lists every setting as it is now, for a UI.
/// <para>
/// A change is checked at once, and copied into the objects the gems hold at the frame's sync point, after the main
/// thread has waited for the render thread and before Sync, so a gem reads its settings on either engine thread and
/// never sees one change halfway. Only a value that differs is copied, and each gem whose settings changed is told on
/// <see cref="Events"/> (<see cref="SettingsChanged"/>). A setting a gem reads only as it starts is marked
/// <see cref="RequireRestartAttribute"/>: changed, the run ends and starts again at once with every setting as it is
/// (<see cref="GetRestartArgs"/>). The default and the applied preset are applied again when their files change. Any
/// thread.
/// </para>
/// </summary>
public sealed class Settings : IDisposable
{
    private const string PresetFolder = "Presets";
    private const string DefaultPreset = "Default";

    private readonly Assets _assets;
    private readonly Args _args;
    private readonly IFileSystem _files;
    private readonly Handle<Preset> _default;
    private readonly JsonObject _commandLine;
    private readonly App _app;
    private readonly Events _events;
    private readonly Subscription _reloads;

    /// <summary>
    /// Held while the layers, the gems' objects, or the changes to copy into them are read or changed.
    /// </summary>
    private readonly Lock _lock = new();

    /// <summary>
    /// Each gem's settings object by the gem's name, as the project lists it, in the order the gems were made.
    /// </summary>
    private readonly OrderedDictionary<string, ISettings> _gems = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Every setting but its value, which is read as <see cref="All"/> is, with the property it is and the object its
    /// gem holds.
    /// </summary>
    private readonly List<(Setting Setting, PropertyInfo Property, ISettings Held)> _all = [];

    /// <summary>
    /// The objects made for changes since the last sync point, by the object their gem holds, which each is copied
    /// into.
    /// </summary>
    private readonly Dictionary<ISettings, ISettings> _changes = new(ReferenceEqualityComparer.Instance);

    private Layers _layers;

    /// <summary>
    /// Reads the default preset, the project's <c>Presets/Default.preset</c> else the engine's, and the one
    /// <c>--preset</c> names, before any gem is made, as each is made from them. A name no preset has is an error, and
    /// only the default is applied.
    /// </summary>
    internal Settings(Assets assets, IFileSystem files, Events events, App app, Args args)
    {
        _assets = assets;
        _args = args;
        _app = app;
        _events = events;
        _files = files;
        _default = assets.Find<Preset>(ToPresetPath(DefaultPreset));
        _commandLine = ParseCommandLine(app.Project, args.Settings);

        Handle<Preset> applied = args.Preset.Length == 0 ? _default : assets.Find<Preset>(ToPresetPath(args.Preset));
        if (applied == Handle<Preset>.None)
        {
            Debugging.Error($"There is no preset {args.Preset}: no asset is {ToPresetPath(args.Preset)}, "
                + "so only the default is applied.");
            applied = _default;
        }

        JsonObject defaults = ReadPresetNow(_default);
        _layers = new Layers(defaults, applied == _default ? defaults : ReadPresetNow(applied), []);
        Preset = applied;
        Debugging.Info($"Preset {GetPresetPath(applied)}");
        _reloads = events.Watch<AssetReloaded<Preset>>(PresetReloaded);

        static string ToPresetPath(string preset) => $"{PresetFolder}/{preset}{Contexts.Assets.Preset.Extensions[0]}";
    }

    /// <summary>
    /// Every preset there is to apply: those in <c>Presets/</c>, the project's and the engine's.
    /// </summary>
    public IReadOnlyList<Handle<Preset>> Presets => FindPresets();

    /// <summary>
    /// The preset applied over the default one; the default itself when no other is.
    /// </summary>
    public Handle<Preset> Preset { get; private set; }

    /// <summary>
    /// Every setting as it is now, what was just set included, gem by gem in the order the gems were made and in a
    /// fixed order within each: what a settings UI lists, each changed with <see cref="TrySet{T}"/> by its key.
    /// Complete once every gem is made.
    /// </summary>
    public IReadOnlyList<Setting> All
    {
        get
        {
            lock (_lock)
            {
                List<Setting> all = new(_all.Count);
                foreach ((Setting setting, PropertyInfo property, ISettings held) in _all)
                {
                    all.Add(setting with { Value = property.GetValue(_changes.GetValueOrDefault(held) ?? held) });
                }

                return all;
            }
        }
    }

    public void Dispose() => _reloads.Dispose();

    /// <summary>
    /// The setting at <paramref name="key"/>, <c>TryGet&lt;bool&gt;("VulkanGpu.hasVsync")</c>. One just set reads back
    /// at once, though its gem sees it only at the next sync point. Why not, when no gem with settings has it, or it is
    /// no <typeparamref name="T"/>.
    /// </summary>
    public Result<T> TryGet<T>(string key)
    {
        if (!IsKey(key)) return Result.Failure<T>(FormatNotKey(key));

        // The gem's settings as JSON, a change not yet copied into them included, so one just set reads back at once.
        JsonNode? node;
        lock (_lock)
        {
            if (!_gems.TryGetValue(GetGem(key), out ISettings? held)) return Result.Failure<T>(FormatNoGem(key));

            ISettings current = _changes.GetValueOrDefault(held) ?? held;
            node = JsonSerializer.SerializeToNode(current, current.GetType(), Json.Options);
        }

        foreach (string name in GetPath(key).Split('.'))
        {
            if (node is not JsonObject level || !level.TryGetPropertyValue(name, out node))
                return Result.Failure<T>($"{GetGem(key)} has no setting {GetPath(key)}.");
        }

        T? value;
        try
        {
            value = node.Deserialize<T>(Json.Options);
        }
        catch (JsonException ex)
        {
            return Result.Failure<T>($"{key} is no {typeof(T).Name}: {ex.Message}");
        }

        return value is null ? Result.Failure<T>($"{key} is null.") : Result.Success(value);
    }

    /// <summary>
    /// Changes the setting at <paramref name="key"/> to <paramref name="value"/> until another preset is applied,
    /// <c>TrySet("VulkanGpu.hasVsync", false)</c>; its gem sees it at the next sync point. The value is anything the
    /// setting reads as JSON, a <see cref="JsonNode"/> included. Why not, when no gem with settings has it or the value
    /// does not fit it; then nothing changes.
    /// </summary>
    public Result TrySet<T>(string key, T value)
    {
        if (!IsKey(key)) return Result.Failure(FormatNotKey(key));

        lock (_lock)
        {
            int index = _gems.IndexOf(GetGem(key));

            if (index < 0) return Result.Failure(FormatNoGem(key));

            // Under the gem's name as listed, so the key replaces the same setting however its gem was spelt.
            (string gem, ISettings held) = _gems.GetAt(index);
            JsonObject live = (JsonObject)_layers.Live.DeepClone();
            live[$"{gem}.{GetPath(key)}"] = JsonSerializer.SerializeToNode(value, Json.Options);
            Layers layers = _layers with { Live = live };
            Result<ISettings> changed = Read(gem, held.GetType(), layers);

            if (!changed.Ok) return Result.Failure($"{key} does not fit {gem}'s settings: {changed.Error}");

            _layers = layers;
            _changes[held] = changed.Value;
        }

        return Result.Success();
    }

    /// <summary>
    /// <see cref="TrySet{T}"/> from one <c>Gem.setting=value</c> line, as <c>--set</c> and a console take it: the value
    /// is JSON when it parses as JSON (<c>1920</c>, <c>true</c>, <c>"1"</c>), and a string when it does not. Why not,
    /// when the line is not one, or as <see cref="TrySet{T}"/> says.
    /// </summary>
    public Result TrySet(string line)
    {
        Result<(string Key, JsonNode? Value)> parsed = Parse(line);

        return parsed.Ok ? TrySet(parsed.Value.Key, parsed.Value.Value) : Result.Failure(parsed.Error);
    }

    /// <summary>
    /// Applies <paramref name="preset"/> over the default one, in place of the one applied: every gem's settings are
    /// made again under it, and what was set while running is dropped. Applying the default leaves it alone. Why not,
    /// when it cannot be read or a gem's settings do not fit it; then nothing changes.
    /// </summary>
    public async Task<Result> ApplyAsync(Handle<Preset> preset, CancellationToken cancel = default)
    {
        Result<JsonObject> read = await ReadAsync(preset, cancel).ConfigureAwait(false);

        if (!read.Ok) return Result.Failure(read.Error);

        lock (_lock)
        {
            Result recreated = Recreate(_layers with { Preset = read.Value, Live = [] });

            if (!recreated.Ok) return Result.Failure($"Preset {GetPresetPath(preset)} {recreated.Error}");

            Preset = preset;
        }

        Debugging.Info($"Preset {GetPresetPath(preset)}");

        return Result.Success();
    }

    /// <summary>
    /// Reads <c>Gem.path=value</c>, as <c>--set</c> takes it: the value is JSON when it parses as JSON (<c>1920</c>,
    /// <c>true</c>, <c>"1"</c>), and a string when it does not.
    /// </summary>
    internal static Result<(string Key, JsonNode? Value)> Parse(string line)
    {
        int equals = line.IndexOf('=', StringComparison.Ordinal);
        string key = equals < 0 ? string.Empty : line[..equals];

        return IsKey(key)
            ? Result.Success((key, ParseValue(line[(equals + 1)..])))
            : Result.Failure<(string, JsonNode?)>($"A setting is Gem.setting=value; '{line}' is not one.");
    }

    /// <summary>
    /// The settings object of type <paramref name="type"/> for <paramref name="gem"/>, kept as the gem's, and each of
    /// its settings added to <see cref="All"/>; nothing for a gem that takes none, which is an error when it is given
    /// some. Why not, when they do not fit it. As the gem is made.
    /// </summary>
    internal Result<ISettings?> Create(string gem, Type? type)
    {
        lock (_lock)
        {
            if (type is null)
            {
                if (GetSection(gem, _layers).Count > 0)
                    Debugging.Error($"Gem {gem} takes no settings, but is given some; they are ignored.");

                return Result.Success<ISettings?>(null);
            }

            Result<ISettings> values = Read(gem, type, _layers);

            if (!values.Ok) return Result.Failure<ISettings?>($"its settings are not valid: {values.Error}");

            _gems.Add(gem, values.Value);
            Dictionary<string, string> summaries = DocComments.Read(_files, type.Assembly);
            foreach (PropertyInfo property in GetSettingProperties(type))
            {
                // Its own attributes first, then its class's, so one on the class marks every setting in it.
                Setting setting = new(
                    GetKeyName(property),
                    DocComments.GetSummary(summaries, property),
                    gem,
                    property.PropertyType,
                    null,
                    [.. property.GetCustomAttributes(), .. type.GetCustomAttributes()]);
                _all.Add((setting, property, values.Value));
            }

            return Result.Success<ISettings?>(values.Value);
        }
    }

    /// <summary>
    /// Copies every change since the last time into the objects the gems hold, and tells each gem whose settings
    /// changed (<see cref="SettingsChanged"/>); when one changes a setting marked <see cref="RequireRestartAttribute"/>,
    /// ends the run to start it again. Main thread, at the frame's sync point.
    /// </summary>
    internal void ApplyChanges()
    {
        string? restart = null;
        lock (_lock)
        {
            if (_changes.Count == 0) return;

            // Each setting of a changed gem whose value differs is copied into the object the gem holds, which is first
            // copied as it was, to tell; one that is the same is left, so whatever the gem holds of it stays the same
            // object. The first that asks for a restart is why the run restarts.
            Dictionary<ISettings, ISettings> before = new(ReferenceEqualityComparer.Instance);
            foreach ((Setting setting, PropertyInfo property, ISettings held) in _all)
            {
                if (!_changes.TryGetValue(held, out ISettings? next)) continue;

                object? value = property.GetValue(next);

                if (IsSame(property.GetValue(held), value, property.PropertyType)) continue;

                before.TryAdd(held, Copy(held));
                if (restart is null && setting.Attributes.OfType<RequireRestartAttribute>().Any())
                    restart = $"{setting.Key} changed";

                property.SetValue(held, value);
            }

            // Told next frame, so a gem's watcher reads them as copied in.
            foreach ((ISettings held, ISettings was) in before)
            {
                _events.Publish(new SettingsChanged(was, _changes[held]));
            }

            _changes.Clear();
        }

        if (restart is { } reason) _app.Restart(reason);
    }

    /// <summary>
    /// The command line that runs the engine again with every setting as it is now: the one this run was given, with
    /// the preset applied, and what was set while running as <c>--set</c> lines after its own, so each still wins.
    /// </summary>
    internal Args GetRestartArgs()
    {
        lock (_lock)
        {
            List<string> lines = [.. _args.Settings];
            foreach ((string key, JsonNode? value) in _layers.Live)
            {
                lines.Add($"{key}={value?.ToJsonString() ?? "null"}");
            }

            string preset = Preset == _default ? string.Empty : Path.GetFileNameWithoutExtension(GetPresetPath(Preset));

            return _args with { Settings = lines, Preset = preset };
        }
    }

    /// <summary>
    /// Whether <paramref name="held"/> and <paramref name="next"/>, values of a setting of <paramref name="type"/>, are
    /// the same: equal, or written alike as JSON, as two lists or maps with the same entries are.
    /// </summary>
    private static bool IsSame(object? held, object? next, Type type)
    {
        return Equals(held, next)
            || JsonNode.DeepEquals(
                JsonSerializer.SerializeToNode(held, type, Json.Options),
                JsonSerializer.SerializeToNode(next, type, Json.Options));
    }

    /// <summary>
    /// A copy of <paramref name="settings"/> as they are now, through JSON, as they were read.
    /// </summary>
    private static ISettings Copy(ISettings settings)
    {
        Type type = settings.GetType();

        return JsonSerializer.SerializeToNode(settings, type, Json.Options).Deserialize(type, Json.Options) as ISettings
            ?? throw new InvalidOperationException($"{type.Name} does not read back as it is written.");
    }

    /// <summary>
    /// Whether <paramref name="key"/> is <c>Gem.path</c>: a gem, then a path of names with dots between them.
    /// </summary>
    private static bool IsKey(string key)
    {
        int dot = key.IndexOf('.', StringComparison.Ordinal);

        return dot > 0 && !key.EndsWith('.') && !key.Contains("..", StringComparison.Ordinal);
    }

    private static string GetGem(string key) => key[..key.IndexOf('.', StringComparison.Ordinal)];

    private static string GetPath(string key) => key[(key.IndexOf('.', StringComparison.Ordinal) + 1)..];

    private static string FormatNotKey(string key) => $"A setting is named Gem.setting; '{key}' is not one.";

    private static string FormatNoGem(string key)
        => $"{GetGem(key)} is no gem with settings, so it has no {GetPath(key)}.";

    /// <summary>
    /// The settings a class of settings holds: its public properties that can be both read and set.
    /// </summary>
    private static IEnumerable<PropertyInfo> GetSettingProperties(Type type)
    {
        return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanRead && property.CanWrite);
    }

    /// <summary>
    /// A setting's name in its key and a preset: its property's name in Core's JSON dialect.
    /// </summary>
    private static string GetKeyName(PropertyInfo property)
        => Json.Options.PropertyNamingPolicy?.ConvertName(property.Name) ?? property.Name;

    /// <summary>
    /// <paramref name="value"/> as JSON when it is JSON (1920, true, ["a"], "quoted"), and as a string when it is not.
    /// </summary>
    private static JsonNode? ParseValue(string value)
    {
        try
        {
            return JsonNode.Parse(value);
        }
        catch (JsonException)
        {
            return JsonValue.Create(value);
        }
    }

    /// <summary>
    /// The command line's settings, each line read as <see cref="Parse"/> does, but those for a gem the project does
    /// not list, which are an error, as a misspelt name would be, and ignored.
    /// </summary>
    private static JsonObject ParseCommandLine(Project project, IReadOnlyList<string> lines)
    {
        JsonObject settings = [];
        foreach (string line in lines)
        {
            Result<(string Key, JsonNode? Value)> parsed = Parse(line);
            if (!parsed.Ok)
            {
                Debugging.Error($"--set {line}: {parsed.Error}");

                continue;
            }

            (string key, JsonNode? value) = parsed.Value;
            if (!project.Gems.Contains(GetGem(key), StringComparer.OrdinalIgnoreCase))
            {
                Debugging.Error(
                    $"--set {key} names {GetGem(key)}, which {project.Name} does not list as a gem; it is ignored.");

                continue;
            }

            settings[key] = value;
        }

        return settings;
    }

    /// <summary>
    /// Writes the settings of <paramref name="layer"/> that are for <paramref name="gem"/> into its
    /// <paramref name="section"/>, each at its path.
    /// </summary>
    private static void WriteInto(JsonObject section, string gem, JsonObject layer)
    {
        foreach ((string key, JsonNode? value) in layer)
        {
            if (!string.Equals(GetGem(key), gem, StringComparison.OrdinalIgnoreCase)) continue;

            // Down the setting's path to the object that holds it, made where no layer before had it.
            string[] path = GetPath(key).Split('.');
            JsonObject parent = section;
            for (int i = 0; i < path.Length - 1; i++)
            {
                if (parent[path[i]] is not JsonObject child)
                {
                    child = [];
                    parent[path[i]] = child;
                }

                parent = child;
            }

            // A node has one parent, and a preset's is the asset's, which nobody changes.
            parent[path[^1]] = value?.DeepClone();
        }
    }

    /// <summary>
    /// <paramref name="preset"/>'s settings, the asset's own object, or why one of its keys is not <c>Gem.path</c>.
    /// </summary>
    private static Result<JsonObject> TryReadPreset(Preset preset, string name)
    {
        foreach ((string key, _) in preset.Settings)
        {
            if (!IsKey(key)) return Result.Failure<JsonObject>($"Preset {name} is not valid: {FormatNotKey(key)}");
        }

        return Result.Success(preset.Settings);
    }

    /// <summary>
    /// Takes in the default or the applied preset as its file now says, keeping what was set while running. One that no
    /// longer fits is an error, and the settings stay as they were. Main thread.
    /// </summary>
    private void PresetReloaded(AssetReloaded<Preset> reloaded)
    {
        Handle<Preset> preset = reloaded.Asset.Handle;
        Result<JsonObject> read = TryReadPreset(reloaded.Asset, GetPresetPath(preset));
        lock (_lock)
        {
            if (preset != _default && preset != Preset) return;

            if (!read.Ok)
            {
                Debugging.Error($"{read.Error}; the settings stay as they were.");

                return;
            }

            // The default, the applied preset, or both, when the default is the one applied.
            Layers layers = _layers;
            layers = preset == _default ? layers with { Default = read.Value } : layers;
            layers = preset == Preset ? layers with { Preset = read.Value } : layers;
            Result recreated = Recreate(layers);
            if (!recreated.Ok)
            {
                Debugging.Error(
                    $"Preset {GetPresetPath(preset)} was changed, but {recreated.Error}; the settings stay as they were.");

                return;
            }
        }

        Debugging.Info($"Preset {GetPresetPath(preset)} applied again, as its file changed");
    }

    /// <summary>
    /// Creates every gem's settings again under <paramref name="layers"/>, which become the layers, and keeps them to
    /// be copied in at the next sync point. Why not, when a gem's do not fit them; then nothing changes. Under the
    /// lock.
    /// </summary>
    private Result Recreate(Layers layers)
    {
        // Every gem's read before any is kept, so one that does not fit them leaves every gem as it was.
        List<(ISettings Held, ISettings Changed)> changes = [];
        foreach ((string gem, ISettings held) in _gems)
        {
            Result<ISettings> changed = Read(gem, held.GetType(), layers);

            if (!changed.Ok) return Result.Failure($"does not fit {gem}'s settings: {changed.Error}");

            changes.Add((held, changed.Value));
        }

        _layers = layers;
        foreach ((ISettings held, ISettings changed) in changes)
        {
            _changes[held] = changed;
        }

        return Result.Success();
    }

    /// <summary>
    /// <paramref name="gem"/>'s settings made from every layer, as a new object of <paramref name="type"/>; or why they
    /// do not fit it.
    /// </summary>
    private Result<ISettings> Read(string gem, Type type, Layers layers)
    {
        object? values;
        try
        {
            values = GetSection(gem, layers).Deserialize(type, Json.Options);
        }
        catch (JsonException ex)
        {
            return Result.Failure<ISettings>(ex.Message);
        }
        catch (NotSupportedException ex)
        {
            return Result.Failure<ISettings>(ex.Message);
        }

        return values is ISettings settings ? Result.Success(settings) : Result.Failure<ISettings>("they are null.");
    }

    /// <summary>
    /// <paramref name="gem"/>'s settings as JSON: every layer's for it written in, in order, the command line's after
    /// the presets'.
    /// </summary>
    private JsonObject GetSection(string gem, Layers layers)
    {
        JsonObject section = [];
        WriteInto(section, gem, layers.Default);
        WriteInto(section, gem, layers.Preset);
        WriteInto(section, gem, _commandLine);
        WriteInto(section, gem, layers.Live);

        return section;
    }

    private List<Handle<Preset>> FindPresets()
    {
        List<Handle<Preset>> presets = [];
        foreach (string path in _assets.GetPaths(PresetFolder))
        {
            Handle<Preset> preset = _assets.Find<Preset>(path);
            if (preset != Handle<Preset>.None) presets.Add(preset);
        }

        return presets;
    }

    /// <summary>
    /// <paramref name="preset"/>'s settings, read as the run starts, before any gem is made, as each is made from them;
    /// none when it cannot be read, which is logged.
    /// </summary>
    [SuppressMessage(
        "Usage",
        "VSTHRD002:Avoid problematic synchronous waits",
        Justification = "The gems are made from the presets, so the run waits for them once, before any gem is made; "
            + "they are read on a worker and need neither engine thread, so the wait cannot deadlock.")]
    private JsonObject ReadPresetNow(Handle<Preset> preset)
    {
        // A missing default has been logged already.
        if (preset == Handle<Preset>.None) return [];

        Result<JsonObject> read = ReadAsync(preset, CancellationToken.None).GetAwaiter().GetResult();
        if (!read.Ok)
        {
            Debugging.Error(read.Error);

            return [];
        }

        return read.Value;
    }

    private async Task<Result<JsonObject>> ReadAsync(Handle<Preset> preset, CancellationToken cancel)
    {
        Result<Preset> loaded = await _assets.LoadAsync(preset, cancel).ConfigureAwait(false);

        return loaded.Ok
            ? TryReadPreset(loaded.Value, GetPresetPath(preset))
            : Result.Failure<JsonObject>($"Preset {GetPresetPath(preset)} could not be read: {loaded.Error}");
    }

    private string GetPresetPath(Handle<Preset> preset)
        => _assets.GetPath(preset) ?? preset.Id.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// What the gems' settings are made from, but the command line's, which never changes: the default preset's
    /// settings, those of the preset applied over it, and what was set while running since, each by key.
    /// </summary>
    private readonly record struct Layers(JsonObject Default, JsonObject Preset, JsonObject Live);
}
