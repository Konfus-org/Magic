using System.Collections.Immutable;
using System.Diagnostics;
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
/// The assets that gems and gameplay use: what the files under the asset directories describe.
/// </summary>
/// <remarks>
/// <para>
/// The asset directories are the project's folder, then each gem's <c>Resources/</c>, then Magic's built-in one; when
/// two hold the same path, the first wins. A file is an asset when a <c>.meta</c> sidecar is beside it. The sidecar
/// holds the 64-bit id that a <see cref="Handle{T}"/> holds, and the import settings that its importer reads. An asset
/// path is relative to its asset directory, with <c>/</c> between folders on every OS. Any thread.
/// </para>
/// <para>
/// Each asset directory is indexed when it is registered, a gem's before the gem is made, so a constructor can find and
/// load an asset. A load of a type that a gem reads, asked for before that gem is made, starts when the gem registers.
/// </para>
/// <para>
/// <see cref="RunImportAsync{T}"/> imports each file on a worker: it reads the file and its <c>.meta</c>, looks in the
/// cache, makes the asset and keeps it in the cache. The <see cref="IAssetImporter{T}"/> that a gem gives for the type
/// makes it, or Core makes it for a type marked <see cref="AssetFormatAttribute"/>. A loaded asset stays while
/// something holds it. When nothing holds it, the GC frees it, and the next load reads it again.
/// </para>
/// <para>
/// The asset directories are watched from when they are registered until the assets are disposed. When their changes
/// settle, they are indexed again. Each loaded asset that is still held and whose file changed, or that was made from
/// one that changed, is made again, after what it was made from. Then <see cref="GetVersion{T}"/> shows the change, a
/// load gets the new asset, and the next frame gets an <see cref="AssetReloaded"/>. An asset that fails to load again
/// keeps what loaded before.
/// </para>
/// <para>
/// <see cref="AssetDatabase"/> holds what assets there are and what depends on what, and <see cref="LoadedAssets"/>
/// holds what was loaded. Each call reads these values as they are. Each change replaces them whole under one lock,
/// held only for the swap. A load lands when its worker finishes, and a file change is taken in when the watches see it
/// settle, so two calls in one frame can see different assets, but each sees a whole one.
/// </para>
/// </remarks>
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
    /// The app whose <see cref="App.Lifetime"/> cancels the loads in progress.
    /// </summary>
    private readonly App _app;

    /// <summary>
    /// Makes the assets with Magic's built-in folder and Core's asset types; register the rest with
    /// <see cref="Register(string)"/>.
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
    /// Whether an asset was asked for and has not landed yet. Any thread.
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
    /// Where the part built as <paramref name="assembly"/> keeps its own assets.
    /// </summary>
    /// <remarks>
    /// Every build but a release records the folder: its source <c>Resources/</c>. Else it is <c>Resources/</c> in
    /// <paramref name="folder"/>, the folder of the assembly, where a release copies them.
    /// </remarks>
    public static string GetResources(IFileSystem files, Assembly assembly, string folder) =>
        assembly.GetMetadata(ResourcesKey) ?? files.Combine(folder, "Resources");

    /// <summary>
    /// Stops the watching.
    /// </summary>
    /// <remarks>
    /// Only the host calls it, at the end of the run, on its thread. The loads in progress stopped with the run's
    /// <see cref="App.Lifetime"/>, and a load that lands after it is dropped. A load that is still awaited ends
    /// cancelled, and so does a load asked for after it.
    /// </remarks>
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
    /// Registers <paramref name="asset"/>, made in memory, as the asset at <paramref name="path"/> until the
    /// subscription is disposed.
    /// </summary>
    /// <remarks>
    /// It is found, named and loaded there with no disk read or write. A file with that path or its id wins. Any
    /// thread.
    /// </remarks>
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
    /// The default asset of type <typeparamref name="T"/>, from Magic's built-in <c>Resources/</c>; every asset type
    /// has one.
    /// </summary>
    /// <remarks>
    /// It is the asset named Default, of any extension, in the folder named for the type: <c>Materials/Default.mat</c>,
    /// or <c>Domains/Default/Default.domain</c> for an asset that is a folder. A project's or a gem's file at that path
    /// is an ordinary asset. When the default is missing, it returns none and logs an error once.
    /// </remarks>
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
    /// The paths of the assets directly in the folder of <paramref name="file"/>, as <see cref="Find{T}"/> takes them,
    /// such as the prefabs of a domain.
    /// </summary>
    /// <remarks>
    /// Call it from the <see cref="IAssetImporter{T}"/> that makes <paramref name="file"/>. The asset is then made
    /// again when a file in the folder changes.
    /// </remarks>
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
    /// Where <paramref name="asset"/> is, as <see cref="Find{T}"/> takes it; null when no file or registered asset has
    /// its id.
    /// </summary>
    public string? GetPath<T>(Handle<T> asset)
        where T : IAsset<T> =>
        Database.Index.ById.TryGetValue(asset.Id, out IndexedFile? found) ? found.Path
        : Loaded.Registered.TryGetValue(asset.Id, out (string Path, object Asset) registered) ? registered.Path
        : null;

    /// <summary>
    /// Which make of <paramref name="asset"/> a load gets: a hash of its file and its <c>.meta</c>; 0 when no file has
    /// it.
    /// </summary>
    /// <remarks>
    /// Compare it with the version that an asset was loaded at to know whether to load it again.
    /// </remarks>
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
    /// levels: 1 for its own, <see cref="AllLevels"/> for all.
    /// </summary>
    /// <remarks>
    /// It gives only what is known now, from what has loaded or been found. To read what has not, use
    /// <see cref="FindDependenciesAsync{TAsset}"/>.
    /// </remarks>
    public ImmutableArray<ulong> GetDependencies<T>(Handle<T> asset, int levels)
        where T : IAsset<T> => [.. Database.GetDependencies(asset.Id, levels)];

    /// <summary>
    /// The ids of what holds handles to <paramref name="asset"/> or was made from it, up to <paramref name="levels"/>
    /// levels: 1 for what names it, <see cref="AllLevels"/> for all.
    /// </summary>
    /// <remarks>
    /// It gives only what is known now, from what has loaded or been found. To read what has not, use
    /// <see cref="FindDependentsAsync{TAsset}"/>.
    /// </remarks>
    public ImmutableArray<ulong> GetDependents<T>(Handle<T> asset, int levels)
        where T : IAsset<T> => [.. Database.GetDependents(asset.Id, levels)];

    /// <summary>
    /// Loads the asset, or tells why it could not be loaded.
    /// </summary>
    /// <remarks>
    /// It is read on a worker now, or when the gem that reads its type registers. It is kept while a caller holds it,
    /// so each caller gets the same object; when no caller holds it, the next load reads it again. A caller that asks
    /// while the asset is made again waits for the new asset. A failed load is remembered until its file changes.
    /// <paramref name="cancel"/> stops only the wait of this caller.
    /// </remarks>
    public Task<Result<T>> LoadAsync<T>(Handle<T> asset, IProgress<LoadProgress>? progress, CancellationToken cancel)
        where T : IAsset<T> => LoadAsync(asset, 0, progress, cancel);

    /// <summary>
    /// Loads <paramref name="asset"/> for an <see cref="IAssetImporter{T}"/> that makes asset
    /// <paramref name="dependent"/> (<see cref="AssetFile.Id"/>), and records that <paramref name="dependent"/> is made
    /// from it.
    /// </summary>
    /// <remarks>
    /// So <paramref name="dependent"/> is made again when <paramref name="asset"/> changes. It refuses when
    /// <paramref name="asset"/> is <paramref name="dependent"/> or is made from it, because each would wait for the
    /// other for ever.
    /// </remarks>
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
    /// The ids of what <paramref name="asset"/> depends on as the files name them, down to <paramref name="levels"/>
    /// levels, or why one could not be read.
    /// </summary>
    /// <remarks>
    /// 1 gives what it names itself, <see cref="AllLevels"/> everything under it. For a type that Core reads, it reads
    /// the file. For a type that a gem reads, it uses what the last import kept, else it loads the asset.
    /// </remarks>
    public Task<Result<ImmutableArray<ulong>>> FindDependenciesAsync<TAsset>(
        Handle<TAsset> asset,
        int levels,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
        where TAsset : IAsset<TAsset> => AssetCrawl.FindDependenciesAsync(this, asset.Id, levels, progress, cancel);

    /// <summary>
    /// The ids of what depends on <paramref name="asset"/> as the files under the asset directories name it, up to
    /// <paramref name="levels"/> levels.
    /// </summary>
    /// <remarks>
    /// 1 gives what names it itself, <see cref="AllLevels"/> everything above it. The first call goes through every
    /// asset. For a type that Core reads, it reads the file. For a type that a gem reads, it uses what the last import
    /// kept, else it loads the asset. An asset that cannot be read counts as one that depends on nothing.
    /// </remarks>
    public Task<ImmutableArray<ulong>> FindDependentsAsync<TAsset>(
        Handle<TAsset> asset,
        int levels,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
        where TAsset : IAsset<TAsset> => AssetCrawl.FindDependentsAsync(this, asset.Id, levels, progress, cancel);

    /// <summary>
    /// Loads what <paramref name="asset"/> depends on, down to <paramref name="levels"/> levels: their ids, or why one
    /// could not be found or loaded.
    /// </summary>
    /// <remarks>
    /// It finds them as <see cref="FindDependenciesAsync{TAsset}"/> does, and loads each as the type its <c>.meta</c>
    /// names.
    /// </remarks>
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
    /// Has <paramref name="preprocessor"/> run on each asset of its type that loads from now on, and on each made
    /// again, until the subscription is disposed. Any thread.
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
    /// What <paramref name="preprocessor"/> made of <paramref name="asset"/>, from the cache, else made now on a worker
    /// and cached.
    /// </summary>
    /// <remarks>
    /// The cache misses when the asset loaded before the preprocessor was registered, or the cache could not keep it.
    /// </remarks>
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
    /// Loads every asset of a type that Magic or a gem reads, so the cache holds what each caching importer and
    /// preprocessor makes.
    /// </summary>
    /// <remarks>
    /// Each asset loads as the type its <c>.meta</c> names. It gives their ids, or why one could not be loaded. A
    /// preprocessor is an <see cref="IAssetPreprocessor{T, TMade}"/>.
    /// </remarks>
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
    /// Loads what depends on <paramref name="asset"/>, up to <paramref name="levels"/> levels: their ids, or why one
    /// could not be loaded.
    /// </summary>
    /// <remarks>
    /// It finds them as <see cref="FindDependentsAsync{TAsset}"/> does, and loads each as the type its <c>.meta</c>
    /// names.
    /// </remarks>
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
    /// Writes <paramref name="asset"/> to <paramref name="file"/>, a path on the machine, with no sidecar, or tells why
    /// it could not.
    /// </summary>
    /// <remarks>
    /// With no sidecar, the file is not an asset. The <see cref="IAssetExporter{T}"/> that a gem gives for the type
    /// writes it, or Core writes it for a type marked <see cref="AssetFormatAttribute"/>. The path ends in the
    /// exporter's extension. Any thread.
    /// </remarks>
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
    /// Adds <paramref name="directory"/> to the asset directories, indexes them again, and watches it.
    /// </summary>
    /// <remarks>
    /// It comes after the directories added before it, which win a path that it shares with them, and before Magic's
    /// built-in one, which all of them win over. A directory that does not exist, or that is added already, is left
    /// out. Game thread.
    /// </remarks>
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

            Log.Info($"Assets indexed: {directory}. {index.ById.Count} assets under {directories.Length} folders.");

            return;
        }
    }

    /// <summary>
    /// Adds the asset types that <paramref name="assembly"/> declares, so a <c>.meta</c> can name them and a crawl can
    /// read them.
    /// </summary>
    /// <remarks>
    /// A name that another type has already stays with that type, and is an error. Game thread.
    /// </remarks>
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
    /// Makes a load of an asset type that one of <paramref name="parts"/> reads wait until that part registers.
    /// </summary>
    /// <remarks>
    /// <paramref name="parts"/> are the types of the gems' parts; their <see cref="IAssetImporter{T}"/>s tell which
    /// asset types they read. The wait ends at <see cref="Register(object)"/>, or when the gem stops it with
    /// <see cref="Drop"/>. A gem made earlier can load such a type. Game thread.
    /// </remarks>
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
    /// Stops the wait for <paramref name="parts"/>, when their gem is made and its parts registered, or was not made.
    /// </summary>
    /// <remarks>
    /// An asset asked for as a type that only they would read, and that no registered part reads, is read now, and
    /// fails as a type that no gem reads. Game thread.
    /// </remarks>
    internal void Drop(IEnumerable<Type> parts)
    {
        lock (_gate)
        {
            Loaded = StartWanted(Database, Loaded with { Expected = Loaded.Expected.RemoveRange(parts) });
        }
    }

    /// <summary>
    /// Makes the gem's <paramref name="part"/> read and write the asset types it imports and exports, until the
    /// subscription is disposed when the gem ends.
    /// </summary>
    /// <remarks>
    /// The part imports a type when it implements <see cref="IAssetImporter{T}"/> of it, and exports a type when it
    /// implements <see cref="IAssetExporter{T}"/> of it. A type that another part reads or writes already stays with
    /// that part, and is an error. An asset asked for as a type that it reads, before anything read it, starts now.
    /// </remarks>
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
    /// Takes in <paramref name="now"/>, the asset directories indexed again after the watches saw
    /// <paramref name="touched"/>, and makes the changed assets again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="touched"/> holds paths from their asset directories. A loaded asset that is still held is read
    /// again when its file changed, is another file now, lists a folder that changed, or was made from one of those,
    /// and so on out. Each is read after what it was made from, because a load waits for the loads in progress. An id
    /// that failed because no file had it is read again too. An asset that nothing holds is forgotten, so the next load
    /// reads it. An asset whose file is gone is let go, and its waiters are told.
    /// </para>
    /// <para>
    /// It returns false and does nothing when the asset directories are no longer <paramref name="directories"/>,
    /// because a directory was added during the read. Any thread.
    /// </para>
    /// </remarks>
    internal bool Reindex(ImmutableArray<string> directories, AssetIndex now, ImmutableHashSet<string> touched)
    {
        List<(TaskCompletionSource<Made> Waiter, Made Gone)> told = [];
        List<string> unloaded = [];
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
                    unloaded.Add(before.ById[id].Path);
                    loaded = loaded with { Entries = loaded.Entries.Remove(id) };
                    continue;
                }

                // One not read yet reads the file as it is now; one being read is read again as that lands.
                if (entry.Stage == AssetStage.Wanted)
                    continue;

                // A loaded asset that nothing holds is let go, not read again: the next ask reads the file as it is. A
                // failed one is read again, so its asker hears when it loads.
                if (entry.Stage == AssetStage.Loaded && entry.GetValue() is null && entry.Error.Length == 0)
                {
                    unloaded.Add(database.GetName(id));
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

        foreach (string asset in unloaded)
        {
            Log.Verbose($"Asset unloaded: {asset}.");
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
    /// Imports asset <paramref name="id"/> as a <typeparamref name="T"/> on a worker, and says what it made.
    /// </summary>
    /// <remarks>
    /// It reads the file and its <c>.meta</c>, looks in the cache, makes the asset, and keeps it in the cache. The gem
    /// that reads the type makes it, or Core makes it for a type marked <see cref="AssetFormatAttribute"/>. Several
    /// imports run at the same time.
    /// </remarks>
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
    /// Reads <paramref name="settings"/>, from a <c>.meta</c>, as the values of the <see cref="AssetMetaAttribute"/>
    /// properties of <typeparamref name="T"/>, or tells why they are not.
    /// </summary>
    /// <remarks>
    /// Each value has the type of its property, and a property that it leaves out takes its initializer. It fails on a
    /// key that no marked property has, or on a value that is not of its property's type.
    /// </remarks>
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
    /// Reads <paramref name="bytes"/> in the <see cref="AssetFileFormat"/> that marks the type, and sets the
    /// <c>.meta</c> values in <paramref name="settings"/> over it.
    /// </summary>
    /// <remarks>
    /// <paramref name="settings"/> is already read against the <see cref="AssetMetaAttribute"/> properties of the type.
    /// When the type has no <see cref="AssetFormatAttribute"/>, it fails: no gem that the project lists reads the type.
    /// </remarks>
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
    /// Writes <paramref name="asset"/> in the <see cref="AssetFileFormat"/> that marks its type: the file that
    /// <see cref="ReadCoreFormat{T}"/> reads back.
    /// </summary>
    /// <remarks>
    /// It leaves out the <see cref="AssetMetaAttribute"/> properties, which the <c>.meta</c> holds. It fails when JSON
    /// cannot hold the asset.
    /// </remarks>
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
    /// The asset types <paramref name="gem"/> implements <paramref name="contract"/> of,
    /// <see cref="IAssetImporter{T}"/> or <see cref="IAssetExporter{T}"/>.
    /// </summary>
    private static ImmutableArray<Type> GetAssetTypes(Type gem, Type contract) =>
        [
            .. gem.GetInterfaces()
                .Where(one => one.IsGenericType && one.GetGenericTypeDefinition() == contract)
                .Select(one => one.GenericTypeArguments[0]),
        ];

    /// <summary>
    /// <paramref name="gems"/> with <paramref name="gem"/> as the one that has each of <paramref name="types"/> no
    /// other gem has already.
    /// </summary>
    private static ImmutableDictionary<Type, object> Claim(
        ImmutableDictionary<Type, object> gems,
        ImmutableArray<Type> types,
        object gem
    ) => types.Aggregate(gems, (held, type) => held.ContainsKey(type) ? held : held.Add(type, gem));

    /// <summary>
    /// <paramref name="gems"/> without <paramref name="gem"/> as the one that has each of <paramref name="types"/>,
    /// where it is.
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
    /// Logs each of <paramref name="types"/> that <paramref name="gems"/>, as <see cref="Claim"/> left them, has
    /// another gem than <paramref name="gem"/> for: that one <paramref name="verb"/> it, and this one does not.
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
    /// the ask recorded.
    /// </summary>
    /// <remarks>
    /// It is a failure when no file has the id; the failure is kept, so the id is read when a file has it. It is a
    /// failure when the asset is another type, or when the importer of <paramref name="dependent"/> asks for it and it
    /// depends on <paramref name="dependent"/>. It is what loaded, while something holds it, or a wait with the others
    /// for a load in progress. Else it is a new entry, read now, or when the gem that reads its type registers, because
    /// the caller can be made before that gem. It is cancelled when the run has ended, because nothing reads it then.
    /// Under <see cref="_gate"/>.
    /// </remarks>
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

        // Kept as a failed entry, so Reindex reads it, and its asker hears, once a file with the id is added.
        if (!database.Index.ById.TryGetValue(id, out IndexedFile? file))
        {
            Made none = Fail($"No asset has the id {id}.");
            AssetEntry failed = new()
            {
                Type = type,
                Import = import,
                Stage = AssetStage.Loaded,
                Error = none.Value.Error,
            };

            return (loaded with { Entries = loaded.Entries.SetItem(id, failed) }, Task.FromResult(none));
        }

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
    /// <paramref name="loaded"/> with the <paramref name="entry"/> of asset <paramref name="id"/> being read on a
    /// worker, to land by <see cref="Land"/>.
    /// </summary>
    /// <remarks>
    /// The gem that reads its type reads it, when there is one. It forgets what the asset was made from, because the
    /// read records that again. Under <see cref="_gate"/>.
    /// </remarks>
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

        Log.Verbose($"Asset loading: {file.Path}.");
        long started = Stopwatch.GetTimestamp();
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

        if (made.Value.Ok)
        {
            Log.Verbose(
                $"Asset loaded: {file.Path}, a {entry.Type.Name}, in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms."
            );
        }

        Land(id, made);
    }

    /// <summary>
    /// Takes in what the read of asset <paramref name="id"/> on a worker made.
    /// </summary>
    /// <remarks>
    /// The asset is loaded and its waiters are told. What it depends on goes in the database and, for a type that a gem
    /// reads, in the cache by its file's version, so a later search does not load it. An asset that had loaded and is
    /// still held, or that had failed, is published as <see cref="AssetReloaded"/>. When a reload fails while the asset
    /// made before is still held, that asset stays in use, and a message says so. An asset whose file changed during
    /// the read is read again instead. An asset that went during the read was told when it went, and a read that lands
    /// after the run is dropped.
    /// </remarks>
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

        if ((before is not null || entry.Error.Length > 0) && made.Value is { Ok: true, Value: { } asset })
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
