using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Magic.Contexts.Assets;
using Magic.Contexts.CmdLine;
using Magic.Contexts.Settings;
using Magic.Contexts.Subscriptions;
using Magic.Contexts.Utils;

namespace Magic.Services;

/// <summary>
/// The gems' settings, and changing them while running: what a game's options use, as a settings window does. A gem
/// with settings declares a class for them, and the object its constructor gets is made here, in layers, each winning
/// over the one before: the class's initialisers, the default <see cref="Contexts.Assets.Preset"/>
/// (<c>Presets/Default.preset</c>, the project's own settings), the preset applied over it, <c>--set</c>, then what is
/// <see cref="Set{T}"/> while running, until another preset is applied. A setting is named by its key,
/// <c>Gem.path</c>: the gem, as the project lists it, then where the setting sits in its settings, with dots between
/// levels.
/// <para>
/// A change is checked at once, and copied into the objects the gems hold at the frame's sync point, after the main
/// thread has waited for the render thread and before LateUpdate, so a gem reads its settings on either engine thread
/// and never sees one change halfway. A setting a gem reads only as it starts is marked
/// <see cref="System.ComponentModel.BrowsableAttribute"/> false, and changes nothing until the next run. The default
/// and the applied preset are applied again when their files change. Any thread.
/// </para>
/// </summary>
public sealed class Settings : IDisposable
{
    private const string PresetFolder = "Presets";

    private readonly Assets _assets;
    private readonly Handle<Preset> _default;
    private readonly IReadOnlyList<Setting> _commandLine;
    private readonly Subscription _reloads;

    /// <summary>
    /// Held while the layers, the gems' objects, or the changes to copy into them are read or changed.
    /// </summary>
    private readonly Lock _lock = new();

    private readonly List<GemSettings> _gems = [];

    /// <summary>
    /// The objects made for changes since the last sync point, by the object their gem holds, which each is copied
    /// into.
    /// </summary>
    private readonly Dictionary<object, object> _changes = new(ReferenceEqualityComparer.Instance);

    private Layers _layers;

    /// <summary>
    /// Reads the default preset and the one <c>--preset</c> names, before any gem is made, as each is made from them. A
    /// name no preset has is an error, and only the default is applied.
    /// </summary>
    internal Settings(Assets assets, Events events, Project project, Args args)
    {
        _assets = assets;
        _default = assets.Default<Preset>();
        _commandLine = CommandLine(project, args.Settings);

        Handle<Preset> applied = args.Preset.Length == 0 ? _default : assets.Find<Preset>($"{PresetFolder}/{args.Preset}{Contexts.Assets.Preset.Extensions[0]}");
        if (applied == Handle<Preset>.None)
        {
            Debugging.Error($"There is no preset {args.Preset}: no asset is {PresetFolder}/{args.Preset}{Contexts.Assets.Preset.Extensions[0]}, so only the default is applied.");
            applied = _default;
        }

        IReadOnlyList<Setting> defaults = ReadNow(_default);
        _layers = new Layers(defaults, applied == _default ? defaults : ReadNow(applied), []);
        Preset = applied;
        Debugging.Info($"Preset {NameOf(applied)}");
        _reloads = events.Watch<AssetReloaded<Preset>>(Reloaded);
    }

    public void Dispose()
    {
        _reloads.Dispose();
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
    /// Each gem with settings, with the object it holds, in the order the gems were made: what a settings window lists.
    /// </summary>
    public IReadOnlyList<GemSettings> Gems => _gems;

    /// <summary>
    /// Reads <c>Gem.path=value</c>, as <c>--set</c> and a console take it: the value is JSON when it parses as JSON
    /// (<c>1920</c>, <c>true</c>, <c>"1"</c>), and a string when it does not.
    /// </summary>
    public static Result<Setting> Parse(string text)
    {
        int equals = text.IndexOf('=', StringComparison.Ordinal);
        string key = equals < 0 ? string.Empty : text[..equals];
        return IsKey(key)
            ? Result.Success(new Setting(key, ValueOf(text[(equals + 1)..])))
            : Result.Failure<Setting>($"A setting is Gem.setting=value; '{text}' is not one.");
    }

    /// <summary>
    /// The setting at <paramref name="key"/>, <c>Get&lt;bool&gt;("SdlPlatform.vsync")</c>. One just set reads back at
    /// once, though its gem sees it only at the next sync point. Why not, when no gem with settings has it, or it is no
    /// <typeparamref name="T"/>.
    /// </summary>
    public Result<T> Get<T>(string key)
    {
        if (!IsKey(key))
        {
            return Result.Failure<T>(NotKey(key));
        }

        JsonNode? node;
        lock (_lock)
        {
            if (Find(GemOf(key)) is not { } gem)
            {
                return Result.Failure<T>(NoGem(key));
            }

            object current = _changes.GetValueOrDefault(gem.Values) ?? gem.Values;
            node = JsonSerializer.SerializeToNode(current, current.GetType(), Json.Options);
        }

        foreach (string name in PathOf(key).Split('.'))
        {
            if (node is not JsonObject level || !level.TryGetPropertyValue(name, out node))
            {
                return Result.Failure<T>($"{GemOf(key)} has no setting {PathOf(key)}.");
            }
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
    /// <c>Set("SdlPlatform.vsync", false)</c>; its gem sees it at the next sync point. Why not, when no gem with
    /// settings has it or the value does not fit it; then nothing changes.
    /// </summary>
    public Result Set<T>(string key, T value)
    {
        if (!IsKey(key))
        {
            return Result.Failure(NotKey(key));
        }

        Setting setting = new(key, JsonSerializer.SerializeToNode(value, Json.Options));
        lock (_lock)
        {
            if (Find(GemOf(key)) is not { } gem)
            {
                return Result.Failure(NoGem(key));
            }

            List<Setting> live = [.. _layers.Live];
            live.RemoveAll(set => IsSameKey(set.Key, key));
            live.Add(setting);
            Layers layers = _layers with { Live = live };
            Result<object> changed = Read(gem.Gem, gem.Values.GetType(), layers);
            if (!changed.Ok)
            {
                return Result.Failure($"{key} does not fit {gem.Gem}'s settings: {changed.Error}");
            }

            _layers = layers;
            _changes[gem.Values] = changed.Value;
        }

        return Result.Success();
    }

    /// <summary>
    /// Applies <paramref name="preset"/> over the default one, in place of the one applied: every gem's settings are
    /// made again under it, and what was set while running is dropped. Applying the default leaves it alone. Why not,
    /// when it cannot be read or a gem's settings do not fit it; then nothing changes.
    /// </summary>
    public async Task<Result> ApplyAsync(Handle<Preset> preset, CancellationToken cancel = default)
    {
        Result<IReadOnlyList<Setting>> read = await ReadAsync(preset, cancel).ConfigureAwait(false);
        if (!read.Ok)
        {
            return Result.Failure(read.Error);
        }

        lock (_lock)
        {
            Result remade = Remake(_layers with { Preset = read.Value, Live = [] });
            if (!remade.Ok)
            {
                return Result.Failure($"Preset {NameOf(preset)} {remade.Error}");
            }

            Preset = preset;
        }

        Debugging.Info($"Preset {NameOf(preset)}");
        return Result.Success();
    }

    /// <summary>
    /// The settings object of type <paramref name="type"/> for <paramref name="gem"/>, kept as the gem's; nothing for a
    /// gem that takes none, which is an error when it is given some. Why not, when they do not fit it. As the gem is
    /// made.
    /// </summary>
    internal Result<object?> Make(string gem, Type? type)
    {
        lock (_lock)
        {
            if (type is null)
            {
                if (Section(gem, _layers).Count > 0)
                {
                    Debugging.Error($"Gem {gem} takes no settings, but is given some; they are ignored.");
                }

                return Result.Success<object?>(null);
            }

            Result<object> values = Read(gem, type, _layers);
            if (!values.Ok)
            {
                return Result.Failure<object?>($"its settings are not valid: {values.Error}");
            }

            _gems.Add(new GemSettings(gem, values.Value));
            return Result.Success<object?>(values.Value);
        }
    }

    /// <summary>
    /// Copies every change since the last time into the objects the gems hold. Main thread, at the frame's sync point.
    /// </summary>
    internal void ApplyChanges()
    {
        lock (_lock)
        {
            foreach ((object held, object changed) in _changes)
            {
                foreach (PropertyInfo property in held.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (property.CanRead && property.CanWrite)
                    {
                        property.SetValue(held, property.GetValue(changed));
                    }
                }
            }

            _changes.Clear();
        }
    }

    /// <summary>
    /// Whether <paramref name="key"/> is <c>Gem.path</c>: a gem, then a path of names with dots between them.
    /// </summary>
    private static bool IsKey(string key)
    {
        int dot = key.IndexOf('.', StringComparison.Ordinal);
        return dot > 0 && !key.EndsWith('.') && !key.Contains("..", StringComparison.Ordinal);
    }

    private static string GemOf(string key)
    {
        return key[..key.IndexOf('.', StringComparison.Ordinal)];
    }

    private static string PathOf(string key)
    {
        return key[(key.IndexOf('.', StringComparison.Ordinal) + 1)..];
    }

    /// <summary>
    /// Whether two keys name the same setting: gem names are matched ignoring case, as the project's list is, and paths
    /// exactly, as the JSON dialect matches keys.
    /// </summary>
    private static bool IsSameKey(string key, string other)
    {
        return string.Equals(GemOf(key), GemOf(other), StringComparison.OrdinalIgnoreCase) && string.Equals(PathOf(key), PathOf(other), StringComparison.Ordinal);
    }

    private static string NotKey(string key)
    {
        return $"A setting is named Gem.setting; '{key}' is not one.";
    }

    private static string NoGem(string key)
    {
        return $"{GemOf(key)} is no gem with settings, so it has no {PathOf(key)}.";
    }

    /// <summary>
    /// <paramref name="value"/> as JSON when it is JSON (1920, true, ["a"], "quoted"), and as a string when it is not.
    /// </summary>
    private static JsonNode? ValueOf(string value)
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
    /// The command line's settings, but those for a gem the project does not list, which are an error, as a misspelt
    /// name would be, and ignored.
    /// </summary>
    private static List<Setting> CommandLine(Project project, IReadOnlyList<Setting> given)
    {
        List<Setting> settings = [];
        foreach (Setting setting in given)
        {
            if (!project.Gems.Contains(GemOf(setting.Key), StringComparer.OrdinalIgnoreCase))
            {
                Debugging.Error($"--set {setting.Key} names {GemOf(setting.Key)}, which {project.Name} does not list as a gem; it is ignored.");
                continue;
            }

            settings.Add(setting);
        }

        return settings;
    }

    /// <summary>
    /// Writes the settings of <paramref name="layer"/> that are for <paramref name="gem"/> into its
    /// <paramref name="section"/>, each at its path.
    /// </summary>
    private static void WriteInto(JsonObject section, string gem, IReadOnlyList<Setting> layer)
    {
        foreach (Setting setting in layer)
        {
            if (!string.Equals(GemOf(setting.Key), gem, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string[] path = PathOf(setting.Key).Split('.');
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

            // A node has one parent, and the preset's is the asset's, which nobody changes.
            parent[path[^1]] = setting.Value?.DeepClone();
        }
    }

    /// <summary>
    /// Takes in the default or the applied preset as its file now says, keeping what was set while running. One that no
    /// longer fits is an error, and the settings stay as they were. Main thread.
    /// </summary>
    private void Reloaded(AssetReloaded<Preset> reloaded)
    {
        Handle<Preset> preset = reloaded.Asset.Handle;
        Result<IReadOnlyList<Setting>> read = SettingsOf(reloaded.Asset);
        lock (_lock)
        {
            if (preset != _default && preset != Preset)
            {
                return;
            }

            if (!read.Ok)
            {
                Debugging.Error($"{read.Error}; the settings stay as they were.");
                return;
            }

            Layers layers = _layers;
            layers = preset == _default ? layers with { Default = read.Value } : layers;
            layers = preset == Preset ? layers with { Preset = read.Value } : layers;
            Result remade = Remake(layers);
            if (!remade.Ok)
            {
                Debugging.Error($"Preset {NameOf(preset)} was changed, but {remade.Error}; the settings stay as they were.");
                return;
            }
        }

        Debugging.Info($"Preset {NameOf(preset)} applied again, as its file changed");
    }

    /// <summary>
    /// Makes every gem's settings again under <paramref name="layers"/>, which become the layers, and keeps them to be
    /// copied in at the next sync point. Why not, when a gem's do not fit them; then nothing changes. Under the lock.
    /// </summary>
    private Result Remake(Layers layers)
    {
        List<(object Held, object Changed)> changes = [];
        foreach (GemSettings gem in _gems)
        {
            Result<object> changed = Read(gem.Gem, gem.Values.GetType(), layers);
            if (!changed.Ok)
            {
                return Result.Failure($"does not fit {gem.Gem}'s settings: {changed.Error}");
            }

            changes.Add((gem.Values, changed.Value));
        }

        _layers = layers;
        foreach ((object held, object changed) in changes)
        {
            _changes[held] = changed;
        }

        return Result.Success();
    }

    /// <summary>
    /// <paramref name="gem"/>'s settings made from every layer, as a new object of <paramref name="type"/>; or why they
    /// do not fit it.
    /// </summary>
    private Result<object> Read(string gem, Type type, Layers layers)
    {
        object? values;
        try
        {
            values = Section(gem, layers).Deserialize(type, Json.Options);
        }
        catch (JsonException ex)
        {
            return Result.Failure<object>(ex.Message);
        }
        catch (NotSupportedException ex)
        {
            return Result.Failure<object>(ex.Message);
        }

        return values is null ? Result.Failure<object>("they are null.") : Result.Success(values);
    }

    /// <summary>
    /// <paramref name="gem"/>'s settings as JSON: every layer's for it written in, in order, the command line's after
    /// the presets'.
    /// </summary>
    private JsonObject Section(string gem, Layers layers)
    {
        JsonObject section = [];
        WriteInto(section, gem, layers.Default);
        WriteInto(section, gem, layers.Preset);
        WriteInto(section, gem, _commandLine);
        WriteInto(section, gem, layers.Live);
        return section;
    }

    private GemSettings? Find(string gem)
    {
        foreach (GemSettings settings in _gems)
        {
            if (string.Equals(settings.Gem, gem, StringComparison.OrdinalIgnoreCase))
            {
                return settings;
            }
        }

        return null;
    }

    private List<Handle<Preset>> FindPresets()
    {
        List<Handle<Preset>> presets = [];
        foreach (string path in _assets.Paths(PresetFolder))
        {
            Handle<Preset> preset = _assets.Find<Preset>(path);
            if (preset != Handle<Preset>.None)
            {
                presets.Add(preset);
            }
        }

        return presets;
    }

    /// <summary>
    /// <paramref name="preset"/>'s settings, read as the run starts, before any gem is made, as each is made from them;
    /// none when it cannot be read, which is logged.
    /// </summary>
    [SuppressMessage("Usage", "VSTHRD002:Avoid problematic synchronous waits", Justification = "The gems are made from the presets, so the run waits for them once, before any gem is made; they are read on a worker and need neither engine thread, so the wait cannot deadlock.")]
    private IReadOnlyList<Setting> ReadNow(Handle<Preset> preset)
    {
        // A missing default has been logged already.
        if (preset == Handle<Preset>.None)
        {
            return [];
        }

        Result<IReadOnlyList<Setting>> read = ReadAsync(preset, CancellationToken.None).GetAwaiter().GetResult();
        if (!read.Ok)
        {
            Debugging.Error(read.Error);
            return [];
        }

        return read.Value;
    }

    private async Task<Result<IReadOnlyList<Setting>>> ReadAsync(Handle<Preset> preset, CancellationToken cancel)
    {
        Result<Preset> loaded = await _assets.LoadAsync(preset, cancel).ConfigureAwait(false);
        return loaded.Ok ? SettingsOf(loaded.Value) : Result.Failure<IReadOnlyList<Setting>>($"Preset {NameOf(preset)} could not be read: {loaded.Error}");
    }

    /// <summary>
    /// <paramref name="preset"/>'s settings, or why one of its keys is not <c>Gem.path</c>.
    /// </summary>
    private Result<IReadOnlyList<Setting>> SettingsOf(Preset preset)
    {
        List<Setting> settings = [];
        foreach ((string key, JsonNode? value) in preset.Settings)
        {
            if (!IsKey(key))
            {
                return Result.Failure<IReadOnlyList<Setting>>($"Preset {NameOf(preset.Handle)} is not valid: {NotKey(key)}");
            }

            settings.Add(new Setting(key, value));
        }

        return Result.Success<IReadOnlyList<Setting>>(settings);
    }

    private string NameOf(Handle<Preset> preset)
    {
        return _assets.Path(preset) ?? preset.Id.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// What the gems' settings are made from, but the command line's, which never changes: the default preset's
    /// settings, those of the preset applied over it, and what was set while running since.
    /// </summary>
    private readonly record struct Layers(IReadOnlyList<Setting> Default, IReadOnlyList<Setting> Preset, IReadOnlyList<Setting> Live);
}
