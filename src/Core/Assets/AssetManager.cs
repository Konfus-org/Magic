using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO.Hashing;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Magic.Assets.Pipeline;
using Magic.Debugging;
using Magic.Events;
using Magic.Files;
using Magic.Projects;
using Magic.Threading;
using Magic.Utils;

namespace Magic.Assets;

/// <summary>
/// The assets, as gems and gameplay use them: what the files under the asset directories describe, the project's folder,
/// then each gem's <c>Resources/</c>, then Magic's built-in one, the first winning a path two of them have. A file is an
/// asset when a <c>.meta</c> sidecar is beside it, holding its 64-bit id, which is what a <see cref="Handle{T}"/> holds,
/// and the import settings its importer reads. An asset path is relative to its asset directory, with <c>/</c> between
/// folders on every OS. Any thread.
/// <para>
/// Every asset directory is indexed as it is registered, a gem's before the gem is made, so finding an asset works in a
/// constructor, and so does loading one: a load of a type a gem reads, asked for before that gem is made, starts as the
/// gem registers.
/// </para>
/// <para>
/// Each file is imported on a worker by <see cref="RunImportAsync{T}"/>: read it, read its <c>.meta</c>, look in the
/// cache, make it, keep it in the cache. It is made by the <see cref="IAssetImporter{T}"/> a gem provides for its type,
/// else by Core itself for a type marked <see cref="AssetFormatAttribute"/>. What loaded is kept while something holds it:
/// once nothing does, the GC frees it, and the next load reads it again.
/// </para>
/// <para>
/// The asset directories are watched from when they are registered until the assets are disposed. Once their changes settle, they are
/// indexed again, and every asset loaded and still held whose file changed, or that was made from one that did, is made again, each after
/// what it was made from: <see cref="GetVersion{T}"/> then says so, a load gets the new one, and the next frame hears
/// an <see cref="AssetReloaded"/>. One that fails to load again keeps what loaded before.
/// </para>
/// <para>
/// What assets there are and what depends on what is its <see cref="AssetDatabase"/>, what was loaded its
/// <see cref="LoadedAssets"/>: values every call reads as they are, each ask replacing them whole under one lock, held
/// only for the swap. A load lands as its worker finishes, and a change to the files is taken in as the watches see it
/// settle, so two calls in one frame can see different assets, each a whole one.
/// </para>
/// </summary>
public sealed class AssetManager : IDisposable
{
    /// <summary>
    /// Every level of dependencies or dependents, however deep: what a crawl's <c>levels</c> takes for all of them.
    /// </summary>
    public const int AllLevels = int.MaxValue;

    /// <summary>
    /// The assembly metadata a build records the folder of its part's assets in, where it does not copy them beside it
    /// (the root <c>Directory.Build.props</c>).
    /// </summary>
    private const string ResourcesKey = "Magic.Resources";

    /// <summary>
    /// Held to swap <see cref="Database"/> and <see cref="Loaded"/>, never across a wait, a file read or an importer.
    /// </summary>
    private readonly Lock _gate = new();

    private readonly AssetWatches _watches;

    /// <summary>
    /// Where an <see cref="AssetReloaded"/> goes for each asset made again.
    /// </summary>
    private readonly EventCoordinator _events;

    /// <summary>
    /// Whose <see cref="App.Lifetime"/> ends the loads on their way.
    /// </summary>
    private readonly App _app;

    /// <summary>
    /// The assets, knowing Magic's built-in folder and Core's own asset types: register the rest (<see cref="Register(string)"/>).
    /// </summary>
    internal AssetManager(IFileSystem files, Caches caches, EventCoordinator events, App app)
    {
        Files = files;
        Caches = caches;
        _events = events;
        _app = app;
        _watches = new AssetWatches(files, this);
        BuiltIn = GetResources(files, typeof(AssetManager).Assembly, AppContext.BaseDirectory);
        Register(BuiltIn);
        Register(typeof(AssetManager).Assembly);
    }

    /// <summary>
    /// Whether a load is on its way, as a replace of the world waits for none to be: an asset asked for that has not
    /// landed. Any thread.
    /// </summary>
    internal bool HasLoadsInFlight => Loaded.Entries.Values.Any(entry => entry.Stage != AssetStage.Loaded);

    /// <summary>
    /// What assets there are and what depends on what, as of now, as any thread reads it. Written under
    /// <see cref="_gate"/> alone.
    /// </summary>
    internal AssetDatabase Database
    {
        get => Volatile.Read(ref field);
        private set => Volatile.Write(ref field, value);
    } = AssetDatabase.Empty;

    /// <summary>
    /// What was asked for and made, as of now, as any thread reads it. Written under <see cref="_gate"/> alone.
    /// </summary>
    internal LoadedAssets Loaded
    {
        get => Volatile.Read(ref field);
        private set => Volatile.Write(ref field, value);
    } = LoadedAssets.Empty;

    internal IFileSystem Files { get; }

    /// <summary>
    /// Where what is slow to make is kept.
    /// </summary>
    internal Caches Caches { get; }

    /// <summary>
    /// Magic's built-in <c>Resources/</c>: where the defaults are, and only there.
    /// </summary>
    internal string BuiltIn { get; }

    /// <summary>
    /// Where the part built as <paramref name="assembly"/> keeps its own assets: the folder its build recorded, its
    /// source <c>Resources/</c>, which every build but a release does; else <c>Resources/</c> in
    /// <paramref name="folder"/>, the assembly's, where a release copies them.
    /// </summary>
    public static string GetResources(IFileSystem files, Assembly assembly, string folder) =>
        assembly.GetMetadata(ResourcesKey) ?? files.Combine(folder, "Resources");

    /// <summary>
    /// Stops the watching: the host's to call, at the end of the run, on its thread. The loads on their way stopped with
    /// the run's <see cref="App.Lifetime"/>, one landing after it is dropped, and a load still waited for ends cancelled,
    /// as one asked for after it does.
    /// </summary>
    public void Dispose()
    {
        _watches.Dispose();
        List<TaskCompletionSource<Made>> waiting;
        lock (_gate)
        {
            waiting = [.. Loaded.Entries.Values.SelectMany(entry => entry.Waiting)];
        }

        foreach (TaskCompletionSource<Made> waiter in waiting)
        {
            waiter.TrySetCanceled(_app.Lifetime);
        }
    }

    /// <summary>
    /// The <typeparamref name="T"/> at <paramref name="path"/>, or none when no asset there is one.
    /// </summary>
    public Handle<T> Find<T>(string path)
        where T : IAsset<T>
    {
        if (Database.Index.ByPath.TryGetValue(path, out IndexedFile? found))
            return found.Type == typeof(T).Name ? new Handle<T>(found.Id) : Handle<T>.None;

        ulong id = GetRegisteredId(path);

        return Loaded.Registered.TryGetValue(id, out (string Path, object Asset) registered) && registered.Asset is T
            ? new Handle<T>(id)
            : Handle<T>.None;
    }

    /// <summary>
    /// Has <paramref name="asset"/>, made in memory, found, named and loaded as the asset at <paramref name="path"/>
    /// while no file has that path or its id, until the subscription is disposed. Nothing reads or writes a disk for it.
    /// Any thread.
    /// </summary>
    public Subscription Register<T>(string path, T asset)
        where T : IAsset<T>
    {
        ulong id = GetRegisteredId(path);
        lock (_gate)
        {
            Loaded = Loaded with { Registered = Loaded.Registered.SetItem(id, (path, asset)) };
        }

        return new Subscription(() =>
        {
            lock (_gate)
            {
                // A later Register of the path replaced this asset, and its own subscription removes it.
                if (
                    Loaded.Registered.TryGetValue(id, out (string Path, object Asset) held)
                    && held.Asset != (object)asset
                )
                    return;

                Loaded = Loaded with { Registered = Loaded.Registered.Remove(id) };
            }
        });
    }

    /// <summary>
    /// <typeparamref name="T"/>'s default: the asset named Default, whatever its extension, in the folder named for the
    /// type, <c>Materials/Default.mat</c>, or <c>Domains/Default/Default.domain</c> for an asset that is a folder, in
    /// Magic's built-in <c>Resources/</c> alone: a project's or a gem's file at that path is an asset like any other.
    /// Every asset type has one. None, and an error logged once, when it is missing.
    /// </summary>
    public Handle<T> GetDefault<T>()
        where T : IAsset<T>
    {
        string folder = typeof(T).Name + "s";
        if (Database.Index.FindBuiltIn(folder, "Default") is { } found && found.Type == typeof(T).Name)
            return new Handle<T>(found.Id);

        lock (_gate)
        {
            LoadedAssets loaded = Loaded;
            if (loaded.ReportedMissing.Contains(folder))
                return Handle<T>.None;

            Loaded = loaded with { ReportedMissing = loaded.ReportedMissing.Add(folder) };
        }

        Log.Error(
            $"There is no default {typeof(T).Name}: no {typeof(T).Name} is {folder}/Default or {folder}/Default/Default "
                + $"under {BuiltIn}."
        );

        return Handle<T>.None;
    }

    /// <summary>
    /// The paths of the assets directly in <paramref name="folder"/>, as <see cref="Find{T}"/> takes them.
    /// </summary>
    public ImmutableArray<string> GetPaths(string folder) =>
        Database.Index.ByFolder.TryGetValue(folder, out ImmutableArray<string> paths) ? paths : [];

    /// <summary>
    /// The paths of the assets in <paramref name="folder"/> and every folder under it, as <see cref="Find{T}"/>
    /// takes them.
    /// </summary>
    public ImmutableArray<string> GetPathsUnder(string folder)
    {
        ImmutableArray<string>.Builder paths = ImmutableArray.CreateBuilder<string>();
        foreach ((string indexed, ImmutableArray<string> files) in Database.Index.ByFolder)
        {
            bool isUnder =
                indexed.Equals(folder, StringComparison.OrdinalIgnoreCase)
                || indexed.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);
            if (isUnder)
                paths.AddRange(files);
        }

        return paths.ToImmutable();
    }

    /// <summary>
    /// The paths of <paramref name="file"/>'s neighbors, the assets directly in its folder, as <see cref="Find{T}"/>
    /// takes them and as the <see cref="IAssetImporter{T}"/> making it reads them: a domain's prefabs. It is made again
    /// when a file in the folder changes.
    /// </summary>
    public ImmutableArray<string> GetNeighbors(AssetFile file)
    {
        string folder = AssetIndex.GetFolder(file.Path);
        lock (_gate)
        {
            LoadedAssets loaded = Loaded;
            if (loaded.Entries.TryGetValue(file.Id, out AssetEntry? entry) && !entry.Folders.Contains(folder))
            {
                Loaded = loaded with
                {
                    Entries = loaded.Entries.SetItem(file.Id, entry with { Folders = entry.Folders.Add(folder) }),
                };
            }
        }

        return GetPaths(folder);
    }

    /// <summary>
    /// Where <paramref name="asset"/> is, as <see cref="Find{T}"/> takes it; null when no file or registered asset has its
    /// id.
    /// </summary>
    public string? GetPath<T>(Handle<T> asset)
        where T : IAsset<T> =>
        Database.Index.ById.TryGetValue(asset.Id, out IndexedFile? found) ? found.Path
        : Loaded.Registered.TryGetValue(asset.Id, out (string Path, object Asset) registered) ? registered.Path
        : null;

    /// <summary>
    /// Which make of <paramref name="asset"/> a load gets: a hash of its file and its <c>.meta</c>, which changes when
    /// either does. Compare it with the version an asset was loaded at to know whether to load it again. 0 when no file
    /// has it.
    /// </summary>
    public ulong GetVersion<T>(Handle<T> asset)
        where T : IAsset<T>
    {
        if (Loaded.Entries.TryGetValue(asset.Id, out AssetEntry? entry) && entry.Current is { } kept)
            return kept.Version;

        if (!Database.Index.ById.TryGetValue(asset.Id, out IndexedFile? file))
            return 0;

        Result<byte[]> bytes = Files.ReadBytes(file.File);

        return bytes.Ok ? AssetIndex.ComputeVersion(bytes.Value, file.Settings) : 0;
    }

    /// <summary>
    /// The ids of what <paramref name="asset"/> holds handles to or was made from, down to <paramref name="levels"/>
    /// levels: 1 for its own, <see cref="AllLevels"/> for everything under it. As far as is known now, from what has
    /// loaded or been found; reading what has not is <see cref="FindDependenciesAsync{TAsset}"/>'s.
    /// </summary>
    public ImmutableArray<ulong> GetDependencies<T>(Handle<T> asset, int levels)
        where T : IAsset<T> => [.. Database.GetDependencies(asset.Id, levels)];

    /// <summary>
    /// The ids of what holds handles to <paramref name="asset"/> or was made from it, up to <paramref name="levels"/>
    /// levels: 1 for what names it itself, <see cref="AllLevels"/> for everything above it. As far as is known now, from
    /// what has loaded or been found; reading what has not is <see cref="FindDependentsAsync{TAsset}"/>'s.
    /// </summary>
    public ImmutableArray<ulong> GetDependents<T>(Handle<T> asset, int levels)
        where T : IAsset<T> => [.. Database.GetDependents(asset.Id, levels)];

    /// <summary>
    /// The asset, or why it could not be loaded. It is read on a worker, from now, or once the gem that reads its type
    /// registers when it is not made yet, and kept while any caller holds it, so each gets the same object; once none does,
    /// the next load reads it again. One asking while it is made again waits for what it is made into. A load that fails is
    /// remembered until its file changes. <paramref name="cancel"/> only stops this caller's wait.
    /// </summary>
    public Task<Result<T>> LoadAsync<T>(Handle<T> asset, IProgress<LoadProgress>? progress, CancellationToken cancel)
        where T : IAsset<T> => LoadAsync(asset, 0, progress, cancel);

    /// <summary>
    /// <paramref name="asset"/>, loaded by an <see cref="IAssetImporter{T}"/> as it makes asset
    /// <paramref name="dependent"/> (<see cref="AssetFile.Id"/>), as the other overload loads it, and recorded as what
    /// <paramref name="dependent"/> is made from, so it is made again when <paramref name="asset"/> changes. Refused when
    /// <paramref name="asset"/> is <paramref name="dependent"/> or is made from it, as each would wait for the other for
    /// ever.
    /// </summary>
    public async Task<Result<T>> LoadAsync<T>(
        Handle<T> asset,
        ulong dependent,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
        where T : IAsset<T>
    {
        if (asset == Handle<T>.None)
            return Result.Failure($"No {typeof(T).Name} was asked for: the handle is none.");

        if (
            !Database.Index.ById.ContainsKey(asset.Id)
            && Loaded.Registered.TryGetValue(asset.Id, out (string Path, object Asset) registered)
            && registered.Asset is T kept
        )
            return Result.Success(kept);

        Made made = await RequestAsync(asset.Id, dependent, typeof(T), RunImportAsync<T>, progress, cancel)
            .ConfigureAwait(false);

        return made.Value.Ok ? Result.Success((T)made.Value.Value) : Result.Failure(made.Value.Error);
    }

    /// <summary>
    /// The ids of what <paramref name="asset"/> depends on, as its file and theirs name them, down to
    /// <paramref name="levels"/> levels: 1 for what it names itself, <see cref="AllLevels"/> for everything under it.
    /// Found by reading the file of a type Core reads, and for a type a gem reads from what its last import kept, else by
    /// loading it; or why one could not be read.
    /// </summary>
    public Task<Result<ImmutableArray<ulong>>> FindDependenciesAsync<TAsset>(
        Handle<TAsset> asset,
        int levels,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
        where TAsset : IAsset<TAsset> => AssetCrawl.FindDependenciesAsync(this, asset.Id, levels, progress, cancel);

    /// <summary>
    /// The ids of what depends on <paramref name="asset"/>, as the files under the asset directories name it, up to
    /// <paramref name="levels"/> levels: 1 for what names it itself, <see cref="AllLevels"/> for everything above it.
    /// Found the first time it is asked by going through every asset: reading the file of a type Core reads, and for a
    /// type a gem reads from what its last import kept, else by loading it. One that cannot be read is taken to depend on
    /// nothing.
    /// </summary>
    public Task<ImmutableArray<ulong>> FindDependentsAsync<TAsset>(
        Handle<TAsset> asset,
        int levels,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
        where TAsset : IAsset<TAsset> => AssetCrawl.FindDependentsAsync(this, asset.Id, levels, progress, cancel);

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

        return found.Ok
            ? await AssetCrawl.LoadAllAsync(this, found.Value, progress, cancel).ConfigureAwait(false)
            : found;
    }

    /// <summary>
    /// Has <paramref name="preprocessor"/> run on each asset of its type that loads from now on, and on each made again,
    /// until the subscription is disposed. Any thread.
    /// </summary>
    public Subscription Register<T, TMade>(IAssetPreprocessor<T, TMade> preprocessor)
        where T : IAsset<T>
    {
        AssetPreprocessRun run = RunAsync;
        lock (_gate)
        {
            Loaded = Loaded with
            {
                Preprocessors = Loaded.Preprocessors.SetItem(
                    typeof(T),
                    Loaded.Preprocessors.GetValueOrDefault(typeof(T), []).Add(run)
                ),
            };
        }

        return new Subscription(() =>
        {
            lock (_gate)
            {
                Loaded = Loaded with
                {
                    Preprocessors = Loaded.Preprocessors.SetItem(
                        typeof(T),
                        Loaded.Preprocessors[typeof(T)].Remove(run)
                    ),
                };
            }
        });

        // What it made of the asset before, by its id and version, else what it makes of it now.
        async Task RunAsync(ulong id, ulong version, object asset, CancellationToken cancel)
        {
            Result<TMade> cached = await Caches.ReadAsync(id, version, preprocessor, cancel).ConfigureAwait(false);
            if (!cached.Ok)
                await Caches
                    .WriteAsync(id, version, preprocessor.Preprocess((T)asset), preprocessor, cancel)
                    .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// What <paramref name="preprocessor"/> made of <paramref name="asset"/> as it loaded, read from the cache; else
    /// the asset is loaded, and what it makes of it made now, on a worker, and cached. The cache misses when the asset
    /// loaded before the preprocessor was registered, or the cache could not keep it.
    /// </summary>
    public async Task<Result<TMade>> LoadPreprocessedAsync<T, TMade>(
        Handle<T> asset,
        IAssetPreprocessor<T, TMade> preprocessor,
        CancellationToken cancel
    )
        where T : IAsset<T>
    {
        ulong version = GetVersion(asset);
        Result<TMade> cached = await Caches.ReadAsync(asset.Id, version, preprocessor, cancel).ConfigureAwait(false);
        if (cached.Ok)
            return cached;

        Result<T> loaded = await LoadAsync(asset, null, cancel).ConfigureAwait(false);
        if (!loaded.Ok)
            return Result.Failure(loaded.Error);

        TMade made = await Task.Run(() => preprocessor.Preprocess(loaded.Value), cancel).ConfigureAwait(false);
        await Caches.WriteAsync(asset.Id, version, made, preprocessor, cancel).ConfigureAwait(false);

        return Result.Success(made);
    }

    /// <summary>
    /// Loads every asset of a type Magic or a gem reads, each as the type its <c>.meta</c> names, so what each importer
    /// that is a cache and each <see cref="IAssetPreprocessor{T, TMade}"/> makes of it is in the cache: their ids, or
    /// why one could not be loaded.
    /// </summary>
    public Task<Result<ImmutableArray<ulong>>> BakeAsync(IProgress<LoadProgress>? progress, CancellationToken cancel)
    {
        LoadedAssets loaded = Loaded;
        ImmutableArray<ulong> readable =
        [
            .. Database
                .Index.ById.Values.Where(file =>
                    loaded.Types.TryGetValue(file.Type, out AssetType? type) && CanImport(type.Type, loaded)
                )
                .Select(file => file.Id),
        ];

        return AssetCrawl.LoadAllAsync(this, readable, progress, cancel);
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

        return await AssetCrawl.LoadAllAsync(this, found, progress, cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes <paramref name="asset"/> to <paramref name="file"/>, a path on the machine, through the
    /// <see cref="IAssetExporter{T}"/> a gem provides for its type, else by Core itself for a type marked
    /// <see cref="AssetFormatAttribute"/>, with no sidecar, so it is a file and not an asset: why not, when it was not
    /// written. The path ends in the exporter's extension. Any thread.
    /// </summary>
    public async Task<Result> ExportAsync<T>(
        T asset,
        string file,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
        where T : IAsset<T>
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(file);

        Result<ReadOnlyMemory<byte>> bytes;
        if (Loaded.Exporters.GetValueOrDefault(typeof(T)) is IAssetExporter<T> exporter)
        {
            if (!file.EndsWith(exporter.Extension, StringComparison.OrdinalIgnoreCase))
                return Result.Failure($"a {typeof(T).Name}'s file ends in {exporter.Extension}, not {file}.");

            bytes = await exporter.ExportAsync(asset, progress, cancel).ConfigureAwait(false);
        }
        else if (typeof(T).GetCustomAttribute<AssetFormatAttribute>() is { } format)
        {
            bytes = await Task.Run(() => WriteCoreFormat(asset, format.Format), cancel).ConfigureAwait(false);
        }
        else
        {
            return Result.Failure($"no gem the project lists writes {typeof(T).Name}s.");
        }

        if (!bytes.Ok)
            return Result.Failure(bytes.Error);

        return await Files.WriteAsync(file, bytes.Value, cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds <paramref name="directory"/> to the asset directories, after those added before it, which win a path it shares
    /// with them, and before Magic's built-in one, which all of them win over; and indexes them, so what it holds is found
    /// from now on, and watches it. One that does not exist, or is added already, is left out. Game thread.
    /// </summary>
    internal void Register(string directory)
    {
        directory = directory.TrimEnd('/', '\\');
        if (!Files.DirectoryExists(directory))
            return;

        if (Database.Directories.Contains(directory, StringComparer.OrdinalIgnoreCase))
            return;

        // Watched before it is read, so nothing written meanwhile is missed.
        _watches.Add(directory);
        while (true)
        {
            AssetDatabase database = Database;
            ImmutableArray<string> directories = database.Directories.IsEmpty
                ? [directory]
                : database.Directories.Insert(database.Directories.Length - 1, directory);
            AssetIndex index = AssetIndex.Create(Files, directories, BuiltIn);
            lock (_gate)
            {
                // Indexed again meanwhile, they are read again, so neither read loses what the other found.
                if (!ReferenceEquals(Database.Index, database.Index))
                    continue;

                Database = Database with { Directories = directories, Index = index };
            }

            Log.Verbose($"Indexed {index.ById.Count} assets under {directories.Length} folders.");

            return;
        }
    }

    /// <summary>
    /// Adds the asset types <paramref name="assembly"/> declares, Magic's or a gem's, so a <c>.meta</c> can name them and the
    /// assets can be crawled without knowing them. A name another type has already stays that one's, and is an error. Game
    /// thread.
    /// </summary>
    internal void Register(Assembly assembly)
    {
        List<AssetType> declared = [];
        foreach (Type type in assembly.GetExportedTypes())
        {
            if (!type.IsClass || type.IsAbstract || !IsAssetType(type))
                continue;

            AssetImportRun import = GetGeneric(nameof(RunImportAsync), type).CreateDelegate<AssetImportRun>(this);
            AssetBuiltInReader readBuiltIn = GetGeneric(nameof(ReadBuiltIn), type).CreateDelegate<AssetBuiltInReader>();
            declared.Add(new AssetType(type, import, readBuiltIn));
        }

        LoadedAssets added;
        lock (_gate)
        {
            added = Loaded;
            foreach (AssetType type in declared)
            {
                if (!added.Types.ContainsKey(type.Type.Name))
                    added = added with { Types = added.Types.Add(type.Type.Name, type) };
            }

            Loaded = added;
        }

        foreach (AssetType type in declared)
        {
            Type first = added.Types[type.Type.Name].Type;
            if (first != type.Type)
            {
                Log.Error(
                    $"{type.Type.FullName} and {first.FullName} are both asset types named {type.Type.Name}, so a "
                        + ".meta's \"type\" names the first."
                );
            }
        }
    }

    /// <summary>
    /// Has a load of an asset type one of <paramref name="parts"/>, the types of the gems' parts, reads, as its
    /// <see cref="IAssetImporter{T}"/>s say, wait for that part until it registers (<see cref="Register(object)"/>) or
    /// its gem stops the wait (<see cref="Drop"/>), as a gem made before it may load one. Game thread.
    /// </summary>
    internal void Expect(IEnumerable<Type> parts)
    {
        List<KeyValuePair<Type, ImmutableArray<Type>>> expected =
        [
            .. parts.Select(part => KeyValuePair.Create(part, GetAssetTypes(part, typeof(IAssetImporter<>)))),
        ];
        lock (_gate)
        {
            Loaded = Loaded with { Expected = Loaded.Expected.SetItems(expected) };
        }
    }

    /// <summary>
    /// Stops waiting for <paramref name="parts"/>, as their gem is made and its parts registered, or was not made: what
    /// was asked for as a type only they would read and no part registered reads is read now, and fails as a type no gem
    /// reads. Game thread.
    /// </summary>
    internal void Drop(IEnumerable<Type> parts)
    {
        lock (_gate)
        {
            Loaded = StartWanted(Database, Loaded with { Expected = Loaded.Expected.RemoveRange(parts) });
        }
    }

    /// <summary>
    /// Has <paramref name="part"/>, a gem's part, read each asset type it implements an <see cref="IAssetImporter{T}"/>
    /// of, and write each it implements an <see cref="IAssetExporter{T}"/> of, until the subscription is disposed, as
    /// the gem ends. A type another part reads or writes already stays that one's, and is an error. What was asked for
    /// as a type it reads before anything read it starts now.
    /// </summary>
    internal Subscription Register(object part)
    {
        ImmutableArray<Type> reads = GetAssetTypes(part.GetType(), typeof(IAssetImporter<>));
        ImmutableArray<Type> writes = GetAssetTypes(part.GetType(), typeof(IAssetExporter<>));
        LoadedAssets claimed;
        lock (_gate)
        {
            LoadedAssets loaded = Loaded;
            claimed = loaded with
            {
                Importers = Claim(loaded.Importers, reads, part),
                Exporters = Claim(loaded.Exporters, writes, part),
            };
            Loaded = StartWanted(Database, claimed);
        }

        LogClaimedByOthers(claimed.Importers, reads, part, "reads");
        LogClaimedByOthers(claimed.Exporters, writes, part, "writes");

        return new Subscription(() =>
        {
            lock (_gate)
            {
                LoadedAssets loaded = Loaded;
                Loaded = loaded with
                {
                    Importers = Release(loaded.Importers, reads, part),
                    Exporters = Release(loaded.Exporters, writes, part),
                };
            }
        });
    }

    /// <summary>
    /// Takes in the asset directories as the watches had them read again, <paramref name="now"/>, after they saw
    /// <paramref name="touched"/>, paths from their asset directories: each asset loaded and still held whose file
    /// changed, is another file now, lists a folder that changed, or was made from one of those, and so on out, is read
    /// again, each after what it was made from, as a load waits for those on their way; one nothing holds, or that
    /// failed, is forgotten, so the next load reads it; one whose file is gone is let go, and its waiters told. Refused,
    /// false, when the asset directories are no longer <paramref name="directories"/>, the ones read: a directory was
    /// added meanwhile. Any thread.
    /// </summary>
    internal bool Reindex(ImmutableArray<string> directories, AssetIndex now, ImmutableHashSet<string> touched)
    {
        List<(TaskCompletionSource<Made> Waiter, Made Gone)> told = [];
        int reloading = 0;
        lock (_gate)
        {
            AssetDatabase database = Database;
            if (database.Directories != directories)
                return false;

            LoadedAssets loaded = Loaded;
            AssetIndex before = database.Index;
            HashSet<ulong> changed = AssetIndex.Diff(before, now, touched);
            changed.UnionWith(loaded.GetListersOf(touched));
            HashSet<ulong> reload = [.. changed, .. loaded.GetMadeFromThese(changed)];
            database = database with { Index = now };
            foreach (ulong id in changed)
            {
                database = database.RemoveDependencies(id);
            }

            foreach (ulong id in reload)
            {
                if (!loaded.Entries.TryGetValue(id, out AssetEntry? entry))
                    continue;

                if (!now.ById.ContainsKey(id))
                {
                    Made gone = Fail($"{before.ById[id].Path} is gone.");
                    told.AddRange(entry.Waiting.Select(waiter => (waiter, gone)));
                    loaded = loaded with { Entries = loaded.Entries.Remove(id) };
                    continue;
                }

                // One not read yet reads the file as it is now; one being read is read again as that lands.
                if (entry.Stage == AssetStage.Wanted)
                    continue;

                // One loaded that nothing holds, or that failed, is let go rather than read again: the next ask reads the
                // file as it is.
                if (entry.Stage == AssetStage.Loaded && entry.GetValue() is null)
                {
                    loaded = loaded with { Entries = loaded.Entries.Remove(id) };

                    continue;
                }

                reloading++;
                loaded =
                    entry.Stage == AssetStage.Loaded
                        ? StartRead(database, loaded, id, entry)
                        : loaded with
                        {
                            Entries = loaded.Entries.SetItem(id, entry with { IsStale = true }),
                        };
            }

            Database = database;
            Loaded = loaded;
        }

        foreach ((TaskCompletionSource<Made> waiter, Made gone) in told)
        {
            waiter.TrySetResult(gone);
        }

        if (reloading > 0)
            Log.Info($"Reloading {reloading} assets.");

        return true;
    }

    /// <summary>
    /// Keeps <paramref name="dependencies"/> as all asset <paramref name="id"/> depends on, found without loading it.
    /// Any thread.
    /// </summary>
    internal void SetDependencies(ulong id, ImmutableArray<ulong> dependencies)
    {
        lock (_gate)
        {
            Database = Database.SetDependencies(id, dependencies);
        }
    }

    /// <summary>
    /// Whether something reads <typeparamref name="T"/>'s files: Core itself, or a gem that is made.
    /// </summary>
    internal bool CanImport<T>()
        where T : IAsset<T> => CanImport(typeof(T), Loaded);

    /// <summary>
    /// Asset <paramref name="id"/> as <paramref name="type"/>, at once when it has loaded, else once it has been
    /// imported; asked for by asset <paramref name="dependent"/>'s importer when that is not 0.
    /// </summary>
    internal async Task<Made> RequestAsync(
        ulong id,
        ulong dependent,
        Type type,
        AssetImportRun import,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
    {
        string work = $"Loading {Database.GetName(id)}";
        progress?.Report(new LoadProgress(work, 0, 1));

        Task<Made> answer;
        lock (_gate)
        {
            (Loaded, answer) = Request(Database, Loaded, id, dependent, type, import, progress);
        }

        Made made = await answer.WaitAsync(cancel).ConfigureAwait(false);
        progress?.Report(new LoadProgress(work, 1, 1));

        return made;
    }

    /// <summary>
    /// Imports asset <paramref name="id"/> as a <typeparamref name="T"/> on a worker, for several assets at once, and says
    /// what it made: reads the file and its <c>.meta</c>, looks in the cache, makes the asset, and keeps what it made in the
    /// cache. It is made by the gem that reads its type, else by Core itself for a type marked
    /// <see cref="AssetFormatAttribute"/>.
    /// </summary>
    internal async Task<Made> RunImportAsync<T>(
        ulong id,
        IndexedFile file,
        object? importer,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
        where T : IAsset<T>
    {
        string notAsset = $"{file.Path} is not a {typeof(T).Name}: ";
        IAssetImporter<T>? reader = importer as IAssetImporter<T>;

        // A file with an extension the type's importer doesn't take is refused before it is read.
        if (
            reader is not null
            && !reader.Extensions.Any(extension => file.Path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        )
        {
            string extensions = string.Join(", ", reader.Extensions);

            return Fail($"{notAsset}a {typeof(T).Name}'s file ends in {extensions}.");
        }

        Result<byte[]> bytes = await Files.ReadBytesAsync(file.File, cancel).ConfigureAwait(false);
        if (!bytes.Ok)
            return Fail($"{file.Path} could not be read: {bytes.Error}");

        ulong version = AssetIndex.ComputeVersion(bytes.Value, file.Settings);
        Result<AssetMetadata> metadata = ReadSettings<T>(file.Settings);
        if (!metadata.Ok)
            return new Made(Result.Failure(notAsset + metadata.Error), version);

        // What was made of the file before, by its id and version, when its importer is also a cache.
        ICache<T>? cache = reader as ICache<T>;
        if (cache is not null)
        {
            Result<T> cached = await Caches.ReadAsync(id, version, cache, cancel).ConfigureAwait(false);
            if (cached.Ok)
                return await PreprocessAsync(id, version, cached.Value, cancel).ConfigureAwait(false);
        }

        Result<T> read = reader is not null
            ? await reader
                .ImportAsync(new AssetFile(id, file.Path, bytes.Value, metadata.Value, version), progress, cancel)
                .ConfigureAwait(false)
            : ReadCoreFormat<T>(bytes.Value, file.Settings);
        if (!read.Ok)
            return new Made(Result.Failure(notAsset + read.Error), version);

        if (cache is not null)
            await Caches.WriteAsync(id, version, read.Value, cache, cancel).ConfigureAwait(false);

        return await PreprocessAsync(id, version, read.Value, cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// <paramref name="bytes"/> read as a <typeparamref name="T"/> by Core itself, with its <c>.meta</c>'s
    /// <paramref name="settings"/> read against the type first.
    /// </summary>
    private static Result<object> ReadBuiltIn<T>(ReadOnlyMemory<byte> bytes, JsonElement settings)
        where T : IAsset<T>
    {
        Result<AssetMetadata> metadata = ReadSettings<T>(settings);
        if (!metadata.Ok)
            return Result.Failure(metadata.Error);

        Result<T> read = ReadCoreFormat<T>(bytes, settings);

        return read.Ok ? Result.Success<object>(read.Value) : Result.Failure(read.Error);
    }

    /// <summary>
    /// <paramref name="settings"/>, a <c>.meta</c>'s, read as the values of <typeparamref name="T"/>'s
    /// <see cref="AssetMetaAttribute"/> properties, each in the type of its property, one it leaves out its
    /// initializer; or why they are not: a key no marked property has, or a value not of its property's type.
    /// </summary>
    private static Result<AssetMetadata> ReadSettings<T>(JsonElement settings)
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
    /// <paramref name="bytes"/> read by Core itself, in the <see cref="AssetFileFormat"/> its type is marked with, with
    /// what its <c>.meta</c> sets, <paramref name="settings"/>, already read against the type's
    /// <see cref="AssetMetaAttribute"/> properties, set over it; else no gem the project lists reads the type.
    /// </summary>
    private static Result<T> ReadCoreFormat<T>(ReadOnlyMemory<byte> bytes, JsonElement settings)
        where T : IAsset<T>
    {
        if (typeof(T).GetCustomAttribute<AssetFormatAttribute>() is not { } format)
            return Result.Failure($"no gem the project lists reads {typeof(T).Name}s.");

        if (format.Format == AssetFileFormat.Json && settings.GetPropertyCount() == 0)
            return Json.Read<T>(bytes.Span);

        JsonObject json;
        if (format.Format == AssetFileFormat.Json)
        {
            Result<JsonObject> read = Json.Read<JsonObject>(bytes.Span);
            if (!read.Ok)
                return Result.Failure(read.Error);

            json = read.Value;
        }
        else
        {
            ReadOnlySpan<byte> text = bytes.Span;
            if (text.StartsWith(Encoding.UTF8.Preamble))
                text = text[Encoding.UTF8.Preamble.Length..];

            json = new JsonObject { ["text"] = Encoding.UTF8.GetString(text) };
        }

        foreach (JsonProperty setting in settings.EnumerateObject())
        {
            json[setting.Name] = JsonNode.Parse(setting.Value.GetRawText());
        }

        return Json.Read<T>(Encoding.UTF8.GetBytes(json.ToJsonString(Json.Options)));
    }

    /// <summary>
    /// <paramref name="asset"/> as Core writes its type itself, in the <see cref="AssetFileFormat"/> it is marked with: the
    /// file <see cref="ReadCoreFormat{T}"/> reads back, without its <see cref="AssetMetaAttribute"/> properties, which its
    /// <c>.meta</c> holds. Why not, when JSON can't hold it.
    /// </summary>
    private static Result<ReadOnlyMemory<byte>> WriteCoreFormat<T>(T asset, AssetFileFormat format)
    {
        Result<JsonElement> written = Json.Write(asset, typeof(T));
        if (!written.Ok)
            return Result.Failure(written.Error);

        if (JsonObject.Create(written.Value) is not { } json)
            return Result.Failure($"a {typeof(T).Name} is not a JSON object.");

        foreach (PropertyInfo property in typeof(T).GetProperties())
        {
            if (property.GetCustomAttribute<AssetMetaAttribute>() is not null)
                json.Remove(JsonNamingPolicy.CamelCase.ConvertName(property.Name));
        }

        if (format == AssetFileFormat.Json)
            return Result.Success<ReadOnlyMemory<byte>>(Encoding.UTF8.GetBytes(json.ToJsonString(Json.Options)));

        return json["text"]?.GetValue<string>() is { } text
            ? Result.Success<ReadOnlyMemory<byte>>(Encoding.UTF8.GetBytes(text))
            : Result.Failure($"a {typeof(T).Name} has no text.");
    }

    /// <summary>
    /// Whether <paramref name="type"/> implements <see cref="IAsset{TSelf}"/> of itself.
    /// </summary>
    private static bool IsAssetType(Type type) =>
        Array.Exists(
            type.GetInterfaces(),
            contract =>
                contract.IsGenericType
                && contract.GetGenericTypeDefinition() == typeof(IAsset<>)
                && contract.GenericTypeArguments[0] == type
        );

    /// <summary>
    /// A generic method of this class's, <paramref name="name"/>, made for <paramref name="type"/>.
    /// </summary>
    private static MethodInfo GetGeneric(string name, Type type) =>
        (
            typeof(AssetManager).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"{nameof(AssetManager)} has no method {name}.")
        ).MakeGenericMethod(type);

    /// <summary>
    /// Whether something reads <paramref name="type"/>'s files: Core itself, or a gem <paramref name="loaded"/> has.
    /// </summary>
    private static bool CanImport(Type type, LoadedAssets loaded) =>
        type.GetCustomAttribute<AssetFormatAttribute>() is not null || loaded.Importers.ContainsKey(type);

    /// <summary>
    /// What a load that made nothing gives: why not.
    /// </summary>
    private static Made Fail(string error) => new(Result.Failure(error), 0);

    /// <summary>
    /// The id of the asset registered at <paramref name="path"/>: a hash of it, in any case. A file with that id wins.
    /// </summary>
    private static ulong GetRegisteredId(string path) =>
        XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(path.ToUpperInvariant()));

    /// <summary>
    /// Whether a load of <paramref name="type"/> waits for a gem: nothing reads it yet, and a gem not made yet will.
    /// </summary>
    private static bool WaitsForGem(Type type, LoadedAssets loaded) =>
        !CanImport(type, loaded) && loaded.Expected.Values.Any(reads => reads.Contains(type));

    /// <summary>
    /// The asset types <paramref name="gem"/> implements <paramref name="contract"/> of, <see cref="IAssetImporter{T}"/>
    /// or <see cref="IAssetExporter{T}"/>.
    /// </summary>
    private static ImmutableArray<Type> GetAssetTypes(Type gem, Type contract) =>
        [
            .. gem.GetInterfaces()
                .Where(one => one.IsGenericType && one.GetGenericTypeDefinition() == contract)
                .Select(one => one.GenericTypeArguments[0]),
        ];

    /// <summary>
    /// <paramref name="gems"/> with <paramref name="gem"/> as the one that has each of <paramref name="types"/> no other
    /// gem has already.
    /// </summary>
    private static ImmutableDictionary<Type, object> Claim(
        ImmutableDictionary<Type, object> gems,
        ImmutableArray<Type> types,
        object gem
    ) => types.Aggregate(gems, (held, type) => held.ContainsKey(type) ? held : held.Add(type, gem));

    /// <summary>
    /// <paramref name="gems"/> without <paramref name="gem"/> as the one that has each of <paramref name="types"/>, where
    /// it is.
    /// </summary>
    private static ImmutableDictionary<Type, object> Release(
        ImmutableDictionary<Type, object> gems,
        ImmutableArray<Type> types,
        object gem
    ) =>
        types.Aggregate(
            gems,
            (held, type) =>
                held.TryGetValue(type, out object? other) && ReferenceEquals(other, gem) ? held.Remove(type) : held
        );

    /// <summary>
    /// Logs each of <paramref name="types"/> that <paramref name="gems"/>, as <see cref="Claim"/> left them, has another
    /// gem than <paramref name="gem"/> for: that one <paramref name="verb"/> it, and this one does not.
    /// </summary>
    private static void LogClaimedByOthers(
        ImmutableDictionary<Type, object> gems,
        ImmutableArray<Type> types,
        object gem,
        string verb
    )
    {
        foreach (Type type in types)
        {
            object other = gems[type];
            if (!ReferenceEquals(other, gem))
            {
                Log.Error(
                    $"{GemRegistry.GetGemName(gem.GetType())} {verb} {type.Name}s, which {GemRegistry.GetGemName(other.GetType())} {verb} already, so it "
                        + "does not."
                );
            }
        }
    }

    /// <summary>
    /// What a load of <paramref name="asset"/> gives, once each <see cref="IAssetPreprocessor{T, TMade}"/> its type has
    /// keeps what it makes of it in the cache.
    /// </summary>
    private async Task<Made> PreprocessAsync<T>(ulong id, ulong version, T asset, CancellationToken cancel)
        where T : IAsset<T>
    {
        foreach (AssetPreprocessRun run in Loaded.Preprocessors.GetValueOrDefault(typeof(T), []))
        {
            await run(id, version, asset, cancel).ConfigureAwait(false);
        }

        return new Made(Result.Success<object>(asset), version, AssetScan.FindHandles(asset));
    }

    /// <summary>
    /// What an ask for asset <paramref name="id"/> as <paramref name="type"/> gets, and <paramref name="loaded"/> with
    /// what it asked: a failure when no file has the id, when it is another type, or, asked for by asset
    /// <paramref name="dependent"/>'s importer, when it depends on what asks for it; what loaded, while something holds
    /// it; a wait with the others for a load on its way; else a new entry, read now, or once the gem expected to read its
    /// type registers, as the one asking may be made before it. Cancelled once the run has ended, as nothing reads it then. Under
    /// <see cref="_gate"/>.
    /// </summary>
    private (LoadedAssets, Task<Made>) Request(
        AssetDatabase database,
        LoadedAssets loaded,
        ulong id,
        ulong dependent,
        Type type,
        AssetImportRun import,
        IProgress<LoadProgress>? progress
    )
    {
        if (_app.Lifetime.IsCancellationRequested)
            return (loaded, Task.FromCanceled<Made>(_app.Lifetime));

        if (dependent != 0)
        {
            if (id == dependent || loaded.IsMadeFrom(id, dependent))
            {
                string name = database.GetName(dependent);

                return (
                    loaded,
                    Task.FromResult(Fail($"{name} depends on itself: something it loads as it is made loads it."))
                );
            }

            // Recorded as what the dependent is made from, so it is made again when this is.
            if (loaded.Entries.TryGetValue(dependent, out AssetEntry? by) && !by.MadeFrom.Contains(id))
            {
                loaded = loaded with
                {
                    Entries = loaded.Entries.SetItem(dependent, by with { MadeFrom = by.MadeFrom.Add(id) }),
                };
            }
        }

        if (!database.Index.ById.TryGetValue(id, out IndexedFile? file))
            return (loaded, Task.FromResult(Fail($"No asset has the id {id}.")));

        if (file.Type != type.Name)
            return (loaded, Task.FromResult(Fail($"{file.Path} is a {file.Type}, so it is not a {type.Name}.")));

        loaded.Entries.TryGetValue(id, out AssetEntry? entry);
        if (entry is not null && entry.Type != type)
            return (
                loaded,
                Task.FromResult(Fail($"{file.Path} is loaded as a {entry.Type.Name}, so it is not a {type.Name}."))
            );

        if (entry is { Stage: AssetStage.Loaded })
        {
            if (entry.GetOutcome() is { } outcome)
                return (loaded, Task.FromResult(outcome));

            // What it made was let go: it is read again, as one never asked for is.
            entry = null;
        }

        TaskCompletionSource<Made> waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (entry is not null)
        {
            entry = entry with { Waiting = entry.Waiting.Add(waiter) };

            return (loaded with { Entries = loaded.Entries.SetItem(id, entry) }, waiter.Task);
        }

        entry = new AssetEntry
        {
            Type = type,
            Import = import,
            Stage = AssetStage.Wanted,
            Progress = progress,
            Waiting = [waiter],
        };
        loaded = loaded with { Entries = loaded.Entries.SetItem(id, entry) };

        return (WaitsForGem(type, loaded) ? loaded : StartRead(database, loaded, id, entry), waiter.Task);
    }

    /// <summary>
    /// <paramref name="loaded"/> with every asset asked for that waited for a gem and no longer does being read: by the
    /// gem that reads its type now, else failing as a type no gem reads. Under <see cref="_gate"/>.
    /// </summary>
    private LoadedAssets StartWanted(AssetDatabase database, LoadedAssets loaded)
    {
        foreach ((ulong id, AssetEntry entry) in loaded.Entries)
        {
            if (entry.Stage == AssetStage.Wanted && !WaitsForGem(entry.Type, loaded))
                loaded = StartRead(database, loaded, id, entry);
        }

        return loaded;
    }

    /// <summary>
    /// <paramref name="loaded"/> with asset <paramref name="id"/>'s <paramref name="entry"/> being read on a worker, by
    /// the gem that reads its type when one does, to land by <see cref="Land"/>; forgetting what it was made from, as
    /// its read records its own. Under <see cref="_gate"/>.
    /// </summary>
    private LoadedAssets StartRead(AssetDatabase database, LoadedAssets loaded, ulong id, AssetEntry entry)
    {
        IndexedFile? file = database.Index.ById.GetValueOrDefault(id);
        object? importer = loaded.Importers.GetValueOrDefault(entry.Type);
        CancellationToken stopped = _app.Lifetime;
        Task.Run(() => ReadFileAsync(id, entry, file, importer, stopped), stopped).FireAndForget();

        return loaded with
        {
            Entries = loaded.Entries.SetItem(
                id,
                entry with
                {
                    Stage = AssetStage.Loading,
                    IsStale = false,
                    MadeFrom = [],
                    Folders = [],
                    Progress = null,
                }
            ),
        };
    }

    /// <summary>
    /// Reads <paramref name="file"/> and makes its asset, on a worker, and lands what it made; one no file has any more
    /// fails.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "An importer that throws on a file fails that file, as content that does not load; the run goes on."
    )]
    private async Task ReadFileAsync(
        ulong id,
        AssetEntry entry,
        IndexedFile? file,
        object? importer,
        CancellationToken stopped
    )
    {
        if (file is null)
        {
            Land(id, Fail($"No asset has the id {id}."));

            return;
        }

        Made made;
        try
        {
            made = await entry.Import(id, file, importer, entry.Progress, stopped).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A cancel as the run ends lands too, and is dropped there with the run's lifetime ended.
            made = Fail($"{file.Path} is not a {entry.Type.Name}: its importer threw: {ex}");
        }

        Land(id, made);
    }

    /// <summary>
    /// Takes in what a worker's read of asset <paramref name="id"/> made: it is loaded, its waiters are told, what it
    /// depends on goes in the database and, for a type a gem reads, the cache, by its file's version, so finding that
    /// later doesn't load it; and one that had loaded and is still held is published as <see cref="AssetReloaded"/>. One
    /// that failed while what it made before is still held leaves that in use, and says so. One whose file changed
    /// meanwhile is read again instead; one gone meanwhile was told as it went, and one landing after the run is dropped.
    /// </summary>
    private void Land(ulong id, Made made)
    {
        AssetEntry? entry;
        AssetEntry done;
        object? before;
        ImmutableArray<ulong> dependencies = default;
        lock (_gate)
        {
            if (!Loaded.Entries.TryGetValue(id, out entry) || _app.Lifetime.IsCancellationRequested)
                return;

            if (entry.IsStale)
            {
                Loaded = StartRead(Database, Loaded, id, entry);

                return;
            }

            before = entry.GetValue();
            if (made.Value.Ok)
            {
                KeptAsset kept = new(new WeakReference<object>(made.Value.Value), made.Version, made.Handles);
                done = entry with { Current = kept, Error = string.Empty };
            }
            else if (before is not null)
                done = entry;
            else
                done = entry with { Current = null, Error = made.Value.Error };

            done = done with { Stage = AssetStage.Loaded, Waiting = [] };
            LoadedAssets loaded = Loaded with { Entries = Loaded.Entries.SetItem(id, done) };
            AssetDatabase database = Database;
            if (made.Value.Ok)
            {
                dependencies = [.. made.Handles.Union(entry.MadeFrom)];
                database = database.SetDependencies(id, dependencies);
            }

            Database = database;
            Loaded = loaded;
        }

        if (!made.Value.Ok && before is not null)
            Log.Error($"{made.Value.Error} What loaded before stays in use.");

        // The waiters get what was made, held here, as the entry holds it only weakly; or what loaded before, a failed
        // reload's.
        Made outcome = made.Value.Ok ? made : done.GetOutcome() ?? made;
        foreach (TaskCompletionSource<Made> waiter in entry.Waiting)
        {
            waiter.TrySetResult(outcome);
        }

        if (before is not null && made.Value is { Ok: true, Value: { } asset })
            _events.Publish(new AssetReloaded(id, asset));

        // What a gem-read asset depends on is kept by its file's version, so finding that later doesn't load it.
        if (dependencies.IsDefault || !Loaded.Importers.ContainsKey(entry.Type))
            return;

        CancellationToken stopped = _app.Lifetime;
        Task.Run(
                () =>
                    Caches.WriteAsync(
                        id,
                        made.Version,
                        new AssetDependencies(dependencies),
                        AssetDependencyCache.Instance,
                        stopped
                    ),
                stopped
            )
            .FireAndForget();
    }
}
