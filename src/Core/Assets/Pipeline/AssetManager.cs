using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Magic.Debugging;
using Magic.Files;
using Magic.Serialization;
using Magic.Threading;
using Magic.Utils;

namespace Magic.Assets.Pipeline;

/// <summary>
/// The assets: what the files under the asset directories describe, the project's <c>Assets/</c> folder, then Magic's
/// built-in <c>Resources/</c>, then each gem's, the first winning a path two of them have. A file is an asset when a
/// <c>.meta</c> sidecar is beside it, holding its 64-bit id, which is what a <see cref="Handle{T}"/> holds, and the
/// import settings its loader reads. An asset path is relative to its asset directory, with <c>/</c> between folders on
/// every OS. Any thread.
/// <para>
/// Every asset directory is indexed before the first gem is made, so finding an asset works in a constructor; a load
/// asked for before the first <see cref="Pump"/>, which comes once every gem is made, waits for it, as the gem that
/// reads its type may be made after the one asking.
/// </para>
/// <para>
/// A file is read by the <see cref="IAssetLoader{T}"/> a gem provides for its type, else by Core itself for a type
/// marked <see cref="AssetFormatAttribute"/>. What loaded is kept for the run.
/// </para>
/// <para>
/// The asset directories are watched until the manager is disposed. Once their changes settle, they are indexed again,
/// and every asset loaded whose file changed, or that depends on one that did, is made again, each after its
/// dependencies: <see cref="GetVersion{T}"/> then says so, and a load gets the new one. One that fails to load again
/// keeps what loaded before.
/// </para>
/// <para>
/// What it knows is one <see cref="AssetState"/> value, which every call reads as it is. It changes in one place,
/// <see cref="Pump"/>, which the host calls once a frame: it folds what happened since, handed to it as data, through
/// <see cref="AssetPipeline.Pump"/>, and runs the actions each returns. So the assets hold still for the whole frame,
/// and a load lands, or an asset changes, only between frames.
/// </para>
/// </summary>
public sealed class AssetManager : IDisposable
{
    /// <summary>
    /// Every level of dependencies or dependents, however deep: what a crawl's <c>levels</c> takes for all of them.
    /// </summary>
    public const int AllLevels = int.MaxValue;

    private const string Sidecar = ".meta";

    /// <summary>
    /// The assembly metadata a build records the folder of its part's assets in, where it does not copy them beside it
    /// (the root <c>Directory.Build.props</c>).
    /// </summary>
    private const string ResourcesKey = "Magic.Resources";

    /// <summary>
    /// How long the files must have been left alone before their changes are taken in: an editor saving a file, or a
    /// build copying a folder, writes several times in a row.
    /// </summary>
    private static readonly TimeSpan _settleTime = TimeSpan.FromMilliseconds(300);

    private readonly IFileSystem _files;

    /// <summary>
    /// Magic's built-in <c>Resources/</c>: where the defaults and failures are, and only there.
    /// </summary>
    private readonly string _builtIn;

    /// <summary>
    /// Ends the loads on their way and the watching, as the manager is disposed.
    /// </summary>
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>
    /// <see cref="_stopping"/>'s token, kept so a load still on its way as the manager is disposed reads it.
    /// </summary>
    private readonly CancellationToken _stopped;

    /// <summary>
    /// What happened, for the next <see cref="Pump"/> to take in.
    /// </summary>
    private readonly Channel<AssetInput> _inputs = Channel.CreateUnbounded<AssetInput>(
        new UnboundedChannelOptions { SingleReader = true }
    );

    /// <summary>
    /// The paths the watches saw change, from their asset directories.
    /// </summary>
    private readonly Channel<string> _touched = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true }
    );

    private readonly Subscriptions _watches = [];

    /// <summary>
    /// What the manager knows: written on the host's thread alone, by <see cref="Pump"/> and before the first.
    /// </summary>
    private AssetState _state = AssetState.Empty;

    /// <summary>
    /// Assets under the directories added (<see cref="AddAssetDirectory"/>), the project's own <c>Assets/</c> then each
    /// gem's <c>Resources/</c>, and last Magic's built-in <c>Resources/</c>.
    /// </summary>
    internal AssetManager(IFileSystem files)
    {
        _files = files;
        _stopped = _stopping.Token;
        _builtIn = GetResources(files, typeof(AssetManager).Assembly, AppContext.BaseDirectory);
        AddAssetTypes(typeof(AssetManager).Assembly);
    }

    /// <summary>
    /// Stops the loads on their way and the watching: the host's to call, at the end of the run, on its thread. A load
    /// still waited for ends cancelled.
    /// </summary>
    public void Dispose()
    {
        _inputs.Writer.TryComplete();
        _touched.Writer.TryComplete();
        _stopping.Cancel();
        _watches.Dispose();
        foreach (AssetEntry entry in _state.Assets.Values)
        {
            foreach (TaskCompletionSource<Made> waiter in entry.Waiting)
            {
                waiter.TrySetCanceled(_stopped);
            }
        }

        while (_inputs.Reader.TryRead(out AssetInput? input))
        {
            if (input is AssetRequested requested)
                requested.Waiter.TrySetCanceled(_stopped);
        }

        _stopping.Dispose();
    }

    /// <summary>
    /// The <typeparamref name="T"/> at <paramref name="path"/>, or none when no asset there is one.
    /// </summary>
    public Handle<T> Find<T>(string path)
        where T : IAsset<T> =>
        Volatile.Read(ref _state).Index.ByPath.TryGetValue(path, out IndexedFile? found) && found.Type == typeof(T).Name
            ? new Handle<T>(found.Id)
            : Handle<T>.None;

    /// <summary>
    /// <typeparamref name="T"/>'s default: the asset named Default, whatever its extension, in the folder named for the
    /// type, <c>Materials/Default.mat</c>, or <c>Domains/Default/Default.domain</c> for an asset that is a folder, in
    /// Magic's built-in <c>Resources/</c> alone: a project's or a gem's file at that path is an asset like any other.
    /// Every asset type has one. None, and an error logged once, when it is missing.
    /// </summary>
    public Handle<T> GetDefault<T>()
        where T : IAsset<T> => FindBuiltIn<T>(typeof(T).Name + "s", "Default", "default");

    /// <summary>
    /// What is drawn in a broken <typeparamref name="TBroken"/>'s place: the <typeparamref name="T"/> named for the
    /// broken type in <typeparamref name="T"/>'s <c>Failures</c> folder, <c>Materials/Failures/Texture.mat</c> for a
    /// texture that does not load, in Magic's built-in <c>Resources/</c> alone, as a default is. Only what a renderer
    /// draws a failure as has one. None, and an error logged once, when it is missing.
    /// </summary>
    public Handle<T> GetFailure<T, TBroken>()
        where T : IAsset<T>
        where TBroken : IAsset<TBroken> =>
        FindBuiltIn<T>($"{typeof(T).Name}s/Failures", typeof(TBroken).Name, $"{typeof(TBroken).Name} failure");

    /// <summary>
    /// The paths of the assets directly in <paramref name="folder"/>, as <see cref="Find{T}"/> takes them: a domain's
    /// prefabs.
    /// </summary>
    public ImmutableArray<string> GetPaths(string folder) =>
        Volatile.Read(ref _state).Index.ByFolder.TryGetValue(folder, out ImmutableArray<string> paths) ? paths : [];

    /// <summary>
    /// Where <paramref name="asset"/> is, as <see cref="Find{T}"/> takes it; null when no file has its id.
    /// </summary>
    public string? GetPath<T>(Handle<T> asset)
        where T : IAsset<T> =>
        Volatile.Read(ref _state).Index.ById.TryGetValue(asset.Id, out IndexedFile? found) ? found.Path : null;

    /// <summary>
    /// Which make of <paramref name="asset"/> a load gets: for one read from a file, a hash of the file and its
    /// <c>.meta</c>, which changes when either does; for one added, a count of the times it was. Compare it with the
    /// version an asset was loaded at to know whether to load it again. 0 when no file and nothing added has it.
    /// </summary>
    public ulong GetVersion<T>(Handle<T> asset)
        where T : IAsset<T>
    {
        AssetState state = Volatile.Read(ref _state);
        if (state.Assets.TryGetValue(asset.Id, out AssetEntry? entry) && entry.Current is { } made)
            return made.Version;

        if (!state.Index.ById.TryGetValue(asset.Id, out IndexedFile? file))
            return 0;

        Result<byte[]> bytes = _files.ReadBytes(file.File);

        return bytes.Ok ? AssetIndex.ComputeVersion(bytes.Value, file.Settings) : 0;
    }

    /// <summary>
    /// The ids of the assets <paramref name="asset"/>'s loader loaded as it made it last, naming it as the dependent:
    /// when one of them is made again, so is it, after it. None for an asset made from its file alone, and for one not
    /// loaded. What its file names, loaded or not, is <see cref="FindDependenciesAsync{TAsset}"/>'s.
    /// </summary>
    public ImmutableArray<ulong> GetDependencies<T>(Handle<T> asset)
        where T : IAsset<T> =>
        Volatile.Read(ref _state).Assets.TryGetValue(asset.Id, out AssetEntry? entry) ? entry.Dependencies : [];

    /// <summary>
    /// The asset, or why it could not be loaded. It is read on a worker once every gem is made, and kept, so every
    /// caller gets the same object; one asking while it is made again waits for what it is made into. A load that
    /// fails is remembered until its file changes. <paramref name="cancel"/> only stops this caller's wait.
    /// </summary>
    public Task<Result<T>> LoadAsync<T>(Handle<T> asset, IProgress<LoadProgress>? progress, CancellationToken cancel)
        where T : IAsset<T> =>
        asset == Handle<T>.None
            ? Task.FromResult<Result<T>>(Result.Failure($"No {typeof(T).Name} was asked for: the handle is none."))
            : LoadIdAsync<T>(asset.Id, 0, progress, cancel);

    /// <summary>
    /// <paramref name="asset"/>, loaded by an <see cref="IAssetLoader{T}"/> as it makes asset
    /// <paramref name="dependent"/> (<see cref="AssetFile.Id"/>), as the other overload loads it, and recorded as what
    /// <paramref name="dependent"/> depends on, so it is made again when <paramref name="asset"/> changes. Refused when
    /// <paramref name="asset"/> is <paramref name="dependent"/> or depends on it, as each would wait for the other for
    /// ever.
    /// </summary>
    public Task<Result<T>> LoadAsync<T>(
        Handle<T> asset,
        ulong dependent,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
        where T : IAsset<T> =>
        asset == Handle<T>.None
            ? LoadAsync(asset, progress, cancel)
            : LoadIdAsync<T>(asset.Id, dependent, progress, cancel);

    /// <summary>
    /// The ids of what <paramref name="asset"/> depends on, as its file and theirs name them, down to
    /// <paramref name="levels"/> levels: 1 for what it names itself, <see cref="AllLevels"/> for everything under it.
    /// Found without loading any of them; or why one could not be read.
    /// </summary>
    public Task<Result<ImmutableArray<ulong>>> FindDependenciesAsync<TAsset>(
        Handle<TAsset> asset,
        int levels,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
        where TAsset : IAsset<TAsset> => CrawlDownAsync(asset.Id, levels, progress, cancel);

    /// <summary>
    /// The ids of what depends on <paramref name="asset"/>, as the files under the asset directories name it, up to
    /// <paramref name="levels"/> levels: 1 for what names it itself, <see cref="AllLevels"/> for everything above it.
    /// Found without loading any of them, reading every asset's file the first time it is asked; one that cannot be
    /// read is taken to depend on nothing.
    /// </summary>
    public async Task<ImmutableArray<ulong>> FindDependentsAsync<TAsset>(
        Handle<TAsset> asset,
        int levels,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
        where TAsset : IAsset<TAsset>
    {
        // What each asset depends on, turned around: who depends on each.
        AssetIndex index = Volatile.Read(ref _state).Index;
        string work = $"Finding what depends on {AssetPipeline.GetName(index, asset.Id)}";
        Dictionary<ulong, List<ulong>> dependents = [];
        int scanned = 0;
        foreach (ulong id in index.ById.Keys)
        {
            Result<ImmutableArray<ulong>> dependencies = await ScanAsync(id, cancel).ConfigureAwait(false);
            progress?.Report(new LoadProgress(work, ++scanned, index.ById.Count));
            if (!dependencies.Ok)
                continue;

            foreach (ulong dependency in dependencies.Value)
            {
                if (!dependents.TryGetValue(dependency, out List<ulong>? of))
                {
                    of = [];
                    dependents.Add(dependency, of);
                }

                of.Add(id);
            }
        }

        HashSet<ulong> found = [];
        List<ulong> level = [asset.Id];
        for (int depth = 0; depth < levels && level.Count > 0; depth++)
        {
            List<ulong> next = [];
            foreach (ulong id in level)
            {
                if (!dependents.TryGetValue(id, out List<ulong>? of))
                    continue;

                foreach (ulong dependent in of)
                {
                    if (dependent != asset.Id && found.Add(dependent))
                        next.Add(dependent);
                }
            }

            level = next;
        }

        return [.. found];
    }

    /// <summary>
    /// Loads what <paramref name="asset"/> depends on, down to <paramref name="levels"/> levels, as
    /// <see cref="FindDependenciesAsync{TAsset}"/> finds it, each as the type its <c>.meta</c> names: their ids, or why
    /// one could not be found or loaded.
    /// </summary>
    public async Task<Result<ImmutableArray<ulong>>> LoadDependenciesAsync<TAsset>(
        Handle<TAsset> asset,
        int levels,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
        where TAsset : IAsset<TAsset>
    {
        Result<ImmutableArray<ulong>> found = await FindDependenciesAsync(asset, levels, progress, cancel)
            .ConfigureAwait(false);

        return found.Ok ? await LoadAllAsync(found.Value, progress, cancel).ConfigureAwait(false) : found;
    }

    /// <summary>
    /// Loads what depends on <paramref name="asset"/>, up to <paramref name="levels"/> levels, as
    /// <see cref="FindDependentsAsync{TAsset}"/> finds it, each as the type its <c>.meta</c> names: their ids, or why
    /// one could not be loaded.
    /// </summary>
    public async Task<Result<ImmutableArray<ulong>>> LoadDependentsAsync<TAsset>(
        Handle<TAsset> asset,
        int levels,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
        where TAsset : IAsset<TAsset>
    {
        ImmutableArray<ulong> found = await FindDependentsAsync(asset, levels, progress, cancel).ConfigureAwait(false);

        return await LoadAllAsync(found, progress, cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// Hands out <paramref name="asset"/>, one a gem made rather than read from a file, under <paramref name="handle"/>,
    /// which the gem derives from what it made it from, so that making it again gives the same one: loading the handle
    /// gets it. Adding another under it replaces it, as a reload does. A handle a file has is an error, and the asset is
    /// not added.
    /// </summary>
    public void Add<T>(Handle<T> handle, T asset)
        where T : IAsset<T>
    {
        if (handle == Handle<T>.None)
            throw new ArgumentException($"A {typeof(T).Name} is added with no handle.", nameof(handle));

        ArgumentNullException.ThrowIfNull(asset);
        Post(new AssetAdded(handle.Id, typeof(T), ReadAsync<T>, asset));
    }

    /// <summary>
    /// Where the part built as <paramref name="assembly"/> keeps its own assets: the folder its build recorded, its
    /// source <c>Resources/</c>, which every build but a release does; else <c>Resources/</c> in
    /// <paramref name="folder"/>, the assembly's, where a release copies them.
    /// </summary>
    internal static string GetResources(IFileSystem files, Assembly assembly, string folder) =>
        assembly.GetMetadata(ResourcesKey) ?? files.Combine(folder, "Resources");

    /// <summary>
    /// Adds <paramref name="directory"/> to the asset directories, after those added before it, which win a path it
    /// shares with them: the project's own <c>Assets/</c>, then each gem's <c>Resources/</c> as the gem loads. One that
    /// does not exist, or is added already, is left out. Only before they are indexed.
    /// </summary>
    internal void AddAssetDirectory(string directory)
    {
        directory = directory.TrimEnd('/', '\\');
        if (!_files.DirectoryExists(directory))
            return;

        foreach (string added in _state.Directories)
        {
            if (string.Equals(added, directory, StringComparison.OrdinalIgnoreCase))
                return;
        }

        _state = _state with { Directories = _state.Directories.Add(directory) };
    }

    /// <summary>
    /// Indexes every asset directory, Magic's built-in <c>Resources/</c> last, which finding an asset waits for, and
    /// from then watches them. Called once, before any gem is made.
    /// </summary>
    internal void Index()
    {
        AddAssetDirectory(_builtIn);

        // Watched before they are read, so nothing written meanwhile is missed.
        foreach (string directory in _state.Directories)
        {
            Result<IFileWatcher> watched = _files.Watch(directory);
            if (!watched.Ok)
            {
                Log.Warn(
                    $"The assets under {directory} are not watched, so a change there is not taken in: {watched.Error}"
                );
                continue;
            }

            IFileWatcher watcher = watched.Value;
            watcher.Created += (_, e) => Touch(directory, e.FullPath);
            watcher.Changed += (_, e) => Touch(directory, e.FullPath);
            watcher.Deleted += (_, e) => Touch(directory, e.FullPath);
            watcher.Renamed += (_, e) =>
            {
                Touch(directory, e.OldFullPath);
                Touch(directory, e.FullPath);
            };
            _watches.Add(new Subscription(watcher.Dispose));
        }

        _state = _state with { Index = AssetIndex.Create(_files, _state.Directories, _builtIn) };
        Log.Info($"Indexed {_state.Index.ById.Count} assets under {_state.Directories.Length} folders.");
        WatchAsync(_stopped).FireAndForget();
    }

    /// <summary>
    /// Has <paramref name="gem"/> read each asset type it implements an <see cref="IAssetLoader{T}"/> of, until the
    /// subscription is disposed, as the gem ends. A type another gem reads already stays that one's, and is an error.
    /// </summary>
    internal Subscription AddLoader(object gem)
    {
        List<Type> reads = [];
        foreach (Type contract in gem.GetType().GetInterfaces())
        {
            if (contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IAssetLoader<>))
                reads.Add(contract.GetGenericArguments()[0]);
        }

        foreach (Type type in reads)
        {
            Post(new AssetLoaderAdded(type, gem));
        }

        return new Subscription(() =>
        {
            foreach (Type type in reads)
            {
                Post(new AssetLoaderRemoved(type, gem));
            }
        });
    }

    /// <summary>
    /// Adds the asset types <paramref name="assembly"/> declares, Magic's or a gem's, so a <c>.meta</c> can name them
    /// and the assets can be crawled without knowing them. A name another type has already stays that one's, and is an
    /// error. Only before the asset directories are indexed.
    /// </summary>
    internal void AddAssetTypes(Assembly assembly)
    {
        foreach (Type type in assembly.GetExportedTypes())
        {
            if (!type.IsClass || type.IsAbstract || !IsAssetType(type))
                continue;

            if (_state.Types.TryGetValue(type.Name, out AssetType? other))
            {
                Log.Error(
                    $"{type.FullName} and {other.Type.FullName} are both asset types named {type.Name}, so a .meta's "
                        + "\"type\" names the first."
                );
                continue;
            }

            AssetReader read = GetGeneric(nameof(ReadAsync), type).CreateDelegate<AssetReader>();
            _state = _state with { Types = _state.Types.Add(type.Name, new AssetType(type, read)) };
        }
    }

    /// <summary>
    /// Takes in what happened since the last pump, through <see cref="AssetPipeline.Pump"/>, publishes the state it
    /// gives and does what it asks: starts reads on workers and gives waiting callers their assets. The host's to call
    /// once a frame, before the gems', on its thread, the first time once every gem is made, so each type's loader is
    /// there: that is when asset loads start.
    /// </summary>
    internal void Pump()
    {
        if (!_state.IsLoadingEnabled)
            Post(new AssetLoadingEnabled());

        while (_inputs.Reader.TryRead(out AssetInput? input))
        {
            (AssetState state, ImmutableArray<AssetAction> actions) = AssetPipeline.Pump(_state, input);
            Volatile.Write(ref _state, state);
            foreach (AssetAction action in actions)
            {
                RunAction(action);
            }
        }
    }

    /// <summary>
    /// <paramref name="file"/> read as a <typeparamref name="T"/>, its <c>.meta</c>'s settings read against the
    /// type's <see cref="AssetMetaAttribute"/> properties first: by <paramref name="loader"/>, the gem that reads the
    /// type, when the file has one of its extensions; else by Core itself (<see cref="ReadBuiltIn{T}"/>).
    /// </summary>
    private static async Task<Result<object>> ReadAsync<T>(
        AssetBytes file,
        object? loader,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
        where T : IAsset<T>
    {
        Result<AssetMetadata> metadata = ReadMetadata<T>(file.Settings);
        if (!metadata.Ok)
            return Result.Failure(metadata.Error);

        Result<T> read;
        if (loader is IAssetLoader<T> reader)
        {
            AssetFile asset = new(file.Id, file.Path, file.Bytes, metadata.Value, file.Version);
            read = HasExtension(file.Path, reader.Extensions)
                ? await reader.LoadAsync(asset, progress, cancel).ConfigureAwait(false)
                : Result.Failure($"a {typeof(T).Name}'s file ends in {string.Join(", ", reader.Extensions)}.");
        }
        else
        {
            read = ReadBuiltIn<T>(file);
        }

        return read.Ok ? Result.Success<object>(read.Value) : Result.Failure(read.Error);
    }

    /// <summary>
    /// <paramref name="settings"/>, a <c>.meta</c>'s, read as the values of <typeparamref name="T"/>'s
    /// <see cref="AssetMetaAttribute"/> properties, each in the type of its property, one it leaves out its
    /// initializer; or why they are not: a key no marked property has, or a value not of its property's type.
    /// </summary>
    private static Result<AssetMetadata> ReadMetadata<T>(JsonElement settings)
        where T : IAsset<T>
    {
        // A type made with its initializers is where the defaults are; one with no parameterless constructor has none.
        object? defaults = typeof(T).GetConstructor(Type.EmptyTypes) is null ? null : Activator.CreateInstance<T>();
        Dictionary<string, PropertyInfo> byKey = new(StringComparer.Ordinal);
        ImmutableDictionary<string, object?>.Builder values = ImmutableDictionary.CreateBuilder<string, object?>(
            StringComparer.Ordinal
        );
        foreach (PropertyInfo property in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetCustomAttribute<AssetMetaAttribute>() is null)
                continue;

            byKey.Add(JsonNamingPolicy.CamelCase.ConvertName(property.Name), property);
            values[property.Name] =
                defaults is not null ? property.GetValue(defaults)
                : property.PropertyType.IsValueType ? Activator.CreateInstance(property.PropertyType)
                : null;
        }

        foreach (JsonProperty setting in settings.EnumerateObject())
        {
            if (!byKey.TryGetValue(setting.Name, out PropertyInfo? property))
            {
                string takes = byKey.Count == 0 ? "none" : string.Join(", ", byKey.Keys);
                return Result.Failure(
                    $"its .meta sets \"{setting.Name}\", which a {typeof(T).Name} does not take; it takes {takes}."
                );
            }

            Result<object> value = Json.Read(setting.Value, property.PropertyType);
            if (!value.Ok)
                return Result.Failure(
                    $"its .meta's \"{setting.Name}\" is not a {property.PropertyType.Name}: {value.Error}"
                );

            values[property.Name] = value.Value;
        }

        return Result.Success(new AssetMetadata(values.ToImmutable()));
    }

    /// <summary>
    /// Whether <paramref name="type"/> implements <see cref="IAsset{TSelf}"/> of itself.
    /// </summary>
    private static bool IsAssetType(Type type)
    {
        foreach (Type contract in type.GetInterfaces())
        {
            if (
                contract.IsGenericType
                && contract.GetGenericTypeDefinition() == typeof(IAsset<>)
                && contract.GetGenericArguments()[0] == type
            )
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// This class's generic method <paramref name="name"/> made for <paramref name="type"/>.
    /// </summary>
    private static MethodInfo GetGeneric(string name, Type type) =>
        (
            typeof(AssetManager).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"AssetManager has no static method {name}.")
        ).MakeGenericMethod(type);

    /// <summary>
    /// <paramref name="file"/> read by Core itself, in the <see cref="AssetFileFormat"/> its type is marked with, with
    /// what its <c>.meta</c> sets, already read against the type's <see cref="AssetMetaAttribute"/> properties, set
    /// over it; else no gem the project lists reads the type.
    /// </summary>
    private static Result<T> ReadBuiltIn<T>(AssetBytes file)
        where T : IAsset<T>
    {
        if (typeof(T).GetCustomAttribute<AssetFormatAttribute>() is not { } format)
            return Result.Failure($"no gem the project lists reads {typeof(T).Name}s.");

        if (format.Format == AssetFileFormat.Json && file.Settings.GetPropertyCount() == 0)
            return Json.Read<T>(file.Bytes.Span);

        JsonObject json;
        if (format.Format == AssetFileFormat.Json)
        {
            Result<JsonObject> read = Json.Read<JsonObject>(file.Bytes.Span);
            if (!read.Ok)
                return Result.Failure(read.Error);

            json = read.Value;
        }
        else
        {
            ReadOnlySpan<byte> text = file.Bytes.Span;
            if (text.StartsWith(Encoding.UTF8.Preamble))
                text = text[Encoding.UTF8.Preamble.Length..];

            json = new JsonObject { ["text"] = Encoding.UTF8.GetString(text) };
        }

        foreach (JsonProperty setting in file.Settings.EnumerateObject())
        {
            json[setting.Name] = JsonNode.Parse(setting.Value.GetRawText());
        }

        Result<JsonElement> element = Json.Write(json, typeof(JsonObject));

        return element.Ok ? Json.Read<T>(element.Value) : Result.Failure(element.Error);
    }

    /// <summary>
    /// Whether <paramref name="path"/> ends in one of <paramref name="extensions"/>.
    /// </summary>
    private static bool HasExtension(string path, ImmutableArray<string> extensions)
    {
        foreach (string extension in extensions)
        {
            if (path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Asset <paramref name="id"/>, at once when it has loaded, else once a pump has it loaded; asked for by asset
    /// <paramref name="dependent"/>'s loader when that is not 0.
    /// </summary>
    private async Task<Result<T>> LoadIdAsync<T>(
        ulong id,
        ulong dependent,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
        where T : IAsset<T>
    {
        Made made = await RequestAsync(id, dependent, typeof(T), ReadAsync<T>, progress, cancel).ConfigureAwait(false);

        return made.Value.Ok ? Result.Success((T)made.Value.Value) : Result.Failure(made.Value.Error);
    }

    /// <summary>
    /// The ids of the handles asset <paramref name="id"/> holds, found by reflection: from the last scan of it while its
    /// file is the same, else from it as it has loaded, else from its file read by Core for a type Core reads, else
    /// from loading it; or why it could not be read.
    /// </summary>
    private async Task<Result<ImmutableArray<ulong>>> ScanAsync(ulong id, CancellationToken cancel)
    {
        AssetState state = Volatile.Read(ref _state);
        if (state.Scanned.TryGetValue(id, out ImmutableArray<ulong> scanned))
            return Result.Success(scanned);

        if (!state.Index.ById.TryGetValue(id, out IndexedFile? file))
            return Result.Failure($"No asset has the id {id}.");

        if (!state.Types.TryGetValue(file.Type, out AssetType? type))
            return Result.Failure(
                $"{file.Path} is a {file.Type}, which neither Magic nor a gem the project lists holds."
            );

        Result<object> asset;
        if (state.Assets.TryGetValue(id, out AssetEntry? entry) && entry.Current is { } current)
        {
            asset = current.Value;
        }
        else if (type.Type.GetCustomAttribute<AssetFormatAttribute>() is not null)
        {
            Result<byte[]> bytes = await _files.ReadBytesAsync(file.File, cancel).ConfigureAwait(false);
            if (!bytes.Ok)
                return Result.Failure($"{file.Path} could not be read: {bytes.Error}");

            ulong version = AssetIndex.ComputeVersion(bytes.Value, file.Settings);
            AssetBytes read = new(id, file.Path, bytes.Value, file.Settings, version);
            asset = await type.Read(read, null, null, cancel).ConfigureAwait(false);
        }
        else if (!state.Loaders.ContainsKey(type.Type))
        {
            // No gem the project lists reads it, so it names nothing that can be found.
            return Result.Success<ImmutableArray<ulong>>([]);
        }
        else
        {
            // TODO(Konfus): keep each gem-read asset's dependency ids in the disk cache, keyed by its file's version,
            // once Caches is ported, so finding them doesn't load it.
            asset = await LoadAnyAsync(id, null, cancel).ConfigureAwait(false);
        }

        if (!asset.Ok)
            return Result.Failure(asset.Error);

        ImmutableArray<ulong> dependencies = AssetScan.FindHandles(asset.Value);
        Post(new AssetDependenciesScanned(id, dependencies));

        return Result.Success(dependencies);
    }

    /// <summary>
    /// The ids <paramref name="id"/> depends on, down to <paramref name="levels"/> levels, scanning each.
    /// </summary>
    private async Task<Result<ImmutableArray<ulong>>> CrawlDownAsync(
        ulong id,
        int levels,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
    {
        string work = $"Finding what {AssetPipeline.GetName(Volatile.Read(ref _state).Index, id)} depends on";
        HashSet<ulong> found = [];
        List<ulong> level = [id];
        int scanned = 0;
        for (int depth = 0; depth < levels && level.Count > 0; depth++)
        {
            List<ulong> next = [];
            foreach (ulong asset in level)
            {
                Result<ImmutableArray<ulong>> dependencies = await ScanAsync(asset, cancel).ConfigureAwait(false);
                if (!dependencies.Ok)
                    return Result.Failure(dependencies.Error);

                progress?.Report(new LoadProgress(work, ++scanned, scanned + level.Count + next.Count));
                foreach (ulong dependency in dependencies.Value)
                {
                    if (dependency != id && found.Add(dependency))
                        next.Add(dependency);
                }
            }

            level = next;
        }

        return Result.Success<ImmutableArray<ulong>>([.. found]);
    }

    /// <summary>
    /// Loads each of <paramref name="ids"/> as the type its <c>.meta</c> names: their ids, or why any could not load.
    /// </summary>
    private async Task<Result<ImmutableArray<ulong>>> LoadAllAsync(
        ImmutableArray<ulong> ids,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
    {
        Task<Result<object>>[] loading = new Task<Result<object>>[ids.Length];
        for (int i = 0; i < ids.Length; i++)
        {
            loading[i] = LoadAnyAsync(ids[i], progress, cancel);
        }

        Result<object>[] loaded = await Task.WhenAll(loading).ConfigureAwait(false);
        List<string> errors = [];
        foreach (Result<object> one in loaded)
        {
            if (!one.Ok)
                errors.Add(one.Error);
        }

        return errors.Count == 0 ? Result.Success(ids) : Result.Failure(string.Join(" ", errors));
    }

    /// <summary>
    /// Asset <paramref name="id"/>, loaded as the type its <c>.meta</c> names.
    /// </summary>
    private async Task<Result<object>> LoadAnyAsync(
        ulong id,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
    {
        AssetState state = Volatile.Read(ref _state);
        if (!state.Index.ById.TryGetValue(id, out IndexedFile? file))
            return Result.Failure($"No asset has the id {id}.");

        if (!state.Types.TryGetValue(file.Type, out AssetType? type))
            return Result.Failure(
                $"{file.Path} is a {file.Type}, which neither Magic nor a gem the project lists holds."
            );

        Made made = await RequestAsync(id, 0, type.Type, type.Read, progress, cancel).ConfigureAwait(false);

        return made.Value;
    }

    /// <summary>
    /// Asset <paramref name="id"/> as <paramref name="type"/>, at once when it has loaded, else once a pump has it
    /// loaded; asked for by asset <paramref name="dependent"/>'s loader when that is not 0.
    /// </summary>
    private async Task<Made> RequestAsync(
        ulong id,
        ulong dependent,
        Type type,
        AssetReader read,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
    {
        AssetState state = Volatile.Read(ref _state);
        string work = $"Loading {AssetPipeline.GetName(state.Index, id)}";
        progress?.Report(new LoadProgress(work, 0, 1));

        // Loaded already, it needs no pump, but a dependency is recorded in one.
        Made made;
        if (
            dependent == 0
            && state.Assets.TryGetValue(id, out AssetEntry? entry)
            && entry.Stage == AssetStage.Loaded
            && entry.Type == type
        )
        {
            made = entry.Outcome;
        }
        else
        {
            TaskCompletionSource<Made> waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(new AssetRequested(id, type, read, progress, waiter, dependent));
            made = await waiter.Task.WaitAsync(cancel).ConfigureAwait(false);
        }

        progress?.Report(new LoadProgress(work, 1, 1));

        return made;
    }

    /// <summary>
    /// The built-in asset named <paramref name="name"/> in <paramref name="folder"/>, as
    /// <see cref="AssetIndex.FindBuiltIn"/> finds it; <paramref name="what"/> says which kind of named asset it is when
    /// it is missing, which a pump says once.
    /// </summary>
    private Handle<T> FindBuiltIn<T>(string folder, string name, string what)
        where T : IAsset<T>
    {
        if (Volatile.Read(ref _state).Index.FindBuiltIn(folder, name) is { } found && found.Type == typeof(T).Name)
            return new Handle<T>(found.Id);

        Post(
            new BuiltInAssetMissing(
                $"{folder}/{name}",
                $"There is no {what} {typeof(T).Name}: no {typeof(T).Name} is {folder}/{name} or {folder}/{name}/{name} "
                    + $"under {_builtIn}."
            )
        );

        return Handle<T>.None;
    }

    /// <summary>
    /// Hands <paramref name="input"/> to the next <see cref="Pump"/>; once the manager is disposed, there is none.
    /// </summary>
    private void Post(AssetInput input) => ObjectDisposedException.ThrowIf(!_inputs.Writer.TryWrite(input), this);

    /// <summary>
    /// Does what a pump asked for.
    /// </summary>
    private void RunAction(AssetAction action)
    {
        switch (action)
        {
            case ReadAssetFile read:
                Task.Run(() => ReadFileAsync(read), _stopped).FireAndForget();
                break;
            case CompleteAssetLoad complete:
                complete.Waiter.TrySetResult(complete.Made);
                break;
            default:
                throw new ArgumentException(
                    $"{action.GetType().Name} is no action the asset manager does.",
                    nameof(action)
                );
        }
    }

    /// <summary>
    /// Reads <paramref name="read"/>'s file and makes its asset, on a worker, and hands what it made to the next pump.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A loader that throws on a file fails that file, as content that does not load; the run goes on."
    )]
    private async Task ReadFileAsync(ReadAssetFile read)
    {
        string path = read.File.Path;
        string type = read.Type.Name;
        Made made;
        try
        {
            Result<byte[]> bytes = await _files.ReadBytesAsync(read.File.File, _stopped).ConfigureAwait(false);
            if (!bytes.Ok)
            {
                made = new Made(Result.Failure($"{path} could not be read: {bytes.Error}"), 0);
            }
            else
            {
                ulong version = AssetIndex.ComputeVersion(bytes.Value, read.File.Settings);
                AssetBytes file = new(read.Id, path, bytes.Value, read.File.Settings, version);
                Result<object> asset = await read.Read(file, read.Loader, read.Progress, _stopped)
                    .ConfigureAwait(false);
                made = new Made(asset.Ok ? asset : Result.Failure($"{path} is not a {type}: {asset.Error}"), version);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            made = new Made(Result.Failure($"{path} is not a {type}: its loader threw: {ex}"), 0);
        }

        _inputs.Writer.TryWrite(new AssetFileRead(read.Id, made));
    }

    /// <summary>
    /// Hands what a watch saw at <paramref name="fullPath"/> under <paramref name="directory"/> to the watching, as a
    /// path from the asset directory, a sidecar's change being its file's.
    /// </summary>
    private void Touch(string directory, string fullPath)
    {
        if (fullPath.Length <= directory.Length)
            return;

        string path = fullPath[(directory.Length + 1)..].Replace('\\', '/');
        if (path.EndsWith(Sidecar, StringComparison.OrdinalIgnoreCase))
            path = path[..^Sidecar.Length];

        _touched.Writer.TryWrite(path);
    }

    /// <summary>
    /// Takes in what the watches see, a batch at a time, once they have seen nothing for <see cref="_settleTime"/>:
    /// indexes the asset directories again, on this worker, and hands the index to the next pump.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Whatever goes wrong taking in one batch is logged, and the next is still taken in."
    )]
    private async Task WatchAsync(CancellationToken cancel)
    {
        ChannelReader<string> touched = _touched.Reader;
        while (await touched.WaitToReadAsync(cancel).ConfigureAwait(false))
        {
            ImmutableHashSet<string>.Builder paths = ImmutableHashSet.CreateBuilder<string>(
                StringComparer.OrdinalIgnoreCase
            );
            do
            {
                while (touched.TryRead(out string? path))
                {
                    paths.Add(path);
                }

                await Task.Delay(_settleTime, cancel).ConfigureAwait(false);
            } while (touched.TryPeek(out _));

            try
            {
                AssetIndex index = AssetIndex.Create(_files, Volatile.Read(ref _state).Directories, _builtIn);
                _inputs.Writer.TryWrite(new AssetDirectoriesReindexed(index, paths.ToImmutable()));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Error($"The asset files' changes could not be taken in: {ex}");
            }
        }
    }
}
